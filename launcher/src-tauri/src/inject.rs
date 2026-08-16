//! Loads the Deadworks client module into a running Deadlock.
//!
//! Downloaded fresh on each connect instead of shipped with the launcher, so
//! people don't have to reinstall for a module fix. Written to temp, injected,
//! deleted once it unloads.
//!
//! We only ever open steam://connect, so there's no process handle to work with
//! and no way to know when the game shows up. Hence the polling: wait for
//! deadlock.exe, wait for panorama.dll inside it, then inject.
//!
//! Nothing here unloads the module. It has to undo its hooks on the engine
//! thread, so it does that itself and calls FreeLibraryAndExitThread.

#![cfg(windows)]

use std::ffi::{c_void, CString};
use std::path::{Path, PathBuf};
use std::thread;
use std::time::{Duration, Instant};

use serde::Deserialize;
use sha2::{Digest, Sha256};

use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Module32FirstW, Module32NextW, Process32FirstW, Process32NextW,
    MODULEENTRY32W, PROCESSENTRY32W, TH32CS_SNAPMODULE, TH32CS_SNAPMODULE32, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress};
use windows_sys::Win32::System::Memory::{
    VirtualAllocEx, VirtualFreeEx, MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_READWRITE,
};
use windows_sys::Win32::System::Threading::{
    CreateRemoteThread, OpenProcess, WaitForSingleObject, INFINITE, PROCESS_CREATE_THREAD,
    PROCESS_QUERY_INFORMATION, PROCESS_VM_OPERATION, PROCESS_VM_READ, PROCESS_VM_WRITE,
};

const GAME_PROCESS: &str = "deadlock.exe";
/// The module hooks this on load, so it has to exist first.
const REQUIRED_MODULE: &str = "panorama.dll";
/// Fixed name so a reconnect can tell "already loaded" from "stale file".
const MODULE_FILE: &str = "deadworks_client.dll";

/// Kept as a constant on purpose. This is code going into the player's game, so
/// the server they're joining doesn't get a say in where it comes from.
/// Test host for now, moves to the Deadworks API later.
const MODULE_MANIFEST_URL: &str = "https://glutensnake.com/ui/client.json";

/// Steam might be mid-update, or the game cold-starting.
const WAIT_FOR_PROCESS: Duration = Duration::from_secs(180);
const WAIT_FOR_MODULE: Duration = Duration::from_secs(120);
const POLL_INTERVAL: Duration = Duration::from_millis(500);
/// Give up waiting to delete rather than leak the wait thread forever.
const WAIT_FOR_UNLOAD: Duration = Duration::from_secs(60 * 60 * 6);
const MAX_MODULE_BYTES: u64 = 16 * 1024 * 1024;

#[derive(Deserialize)]
struct ModuleManifest {
    url: String,
    sha256: String,
    #[serde(default)]
    version: String,
}

/// Off unless the player asked for it. We're loading code into their game.
pub(crate) fn is_enabled(app: &tauri::AppHandle) -> bool {
    use tauri_plugin_store::StoreBuilder;
    StoreBuilder::new(app, "settings.json")
        .build()
        .ok()
        .and_then(|store| store.get("custom_ui_enabled"))
        .and_then(|v| v.as_bool())
        .unwrap_or(false)
}

fn module_path() -> PathBuf {
    std::env::temp_dir().join(MODULE_FILE)
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

/// Download and hash-check. Nothing lands on disk unless the hash matches.
fn fetch_module(dest: &Path) -> Result<String, String> {
    let client = reqwest::blocking::Client::builder()
        .timeout(Duration::from_secs(60))
        .build()
        .map_err(|e| format!("http client: {e}"))?;

    let manifest: ModuleManifest = client
        .get(MODULE_MANIFEST_URL)
        .send()
        .and_then(|r| r.error_for_status())
        .map_err(|e| format!("manifest fetch failed: {e}"))?
        .json()
        .map_err(|e| format!("manifest is not valid json: {e}"))?;

    if !manifest.url.starts_with("https://") {
        return Err("module url is not https".into());
    }
    if manifest.sha256.len() != 64 || !manifest.sha256.chars().all(|c| c.is_ascii_hexdigit()) {
        return Err("module manifest has a malformed sha256".into());
    }

    let response = client
        .get(&manifest.url)
        .send()
        .and_then(|r| r.error_for_status())
        .map_err(|e| format!("module download failed: {e}"))?;

    if let Some(len) = response.content_length() {
        if len > MAX_MODULE_BYTES {
            return Err(format!("module is {len} bytes, over the cap"));
        }
    }
    let bytes = response
        .bytes()
        .map_err(|e| format!("module download failed: {e}"))?;
    if bytes.len() as u64 > MAX_MODULE_BYTES {
        return Err("module exceeds the size cap".into());
    }

    let actual = hex(&Sha256::digest(&bytes));
    if !actual.eq_ignore_ascii_case(&manifest.sha256) {
        return Err(format!(
            "module REJECTED - sha256 mismatch (expected {}, got {actual})",
            manifest.sha256
        ));
    }

    std::fs::write(dest, &bytes).map_err(|e| format!("could not write {}: {e}", dest.display()))?;
    Ok(manifest.version)
}

struct Snapshot(HANDLE);

impl Snapshot {
    fn new(flags: u32, pid: u32) -> Option<Self> {
        let handle = unsafe { CreateToolhelp32Snapshot(flags, pid) };
        (handle != INVALID_HANDLE_VALUE).then_some(Self(handle))
    }
}

impl Drop for Snapshot {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

fn wide_to_string(raw: &[u16]) -> String {
    let len = raw.iter().position(|&c| c == 0).unwrap_or(raw.len());
    String::from_utf16_lossy(&raw[..len])
}

fn find_process(name: &str) -> Option<u32> {
    let snapshot = Snapshot::new(TH32CS_SNAPPROCESS, 0)?;
    let mut entry: PROCESSENTRY32W = unsafe { std::mem::zeroed() };
    entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;

    if unsafe { Process32FirstW(snapshot.0, &mut entry) } == 0 {
        return None;
    }
    loop {
        if wide_to_string(&entry.szExeFile).eq_ignore_ascii_case(name) {
            return Some(entry.th32ProcessID);
        }
        if unsafe { Process32NextW(snapshot.0, &mut entry) } == 0 {
            return None;
        }
    }
}

/// None means we couldn't read the module list at all, which happens while a
/// process is still starting up (or after it exits). That's not the same as
/// "module isn't there" - treat it as such and you inject too early.
fn has_module(pid: u32, name: &str) -> Option<bool> {
    let snapshot = Snapshot::new(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid)?;
    let mut entry: MODULEENTRY32W = unsafe { std::mem::zeroed() };
    entry.dwSize = std::mem::size_of::<MODULEENTRY32W>() as u32;

    if unsafe { Module32FirstW(snapshot.0, &mut entry) } == 0 {
        return None;
    }
    loop {
        if wide_to_string(&entry.szModule).eq_ignore_ascii_case(name) {
            return Some(true);
        }
        if unsafe { Module32NextW(snapshot.0, &mut entry) } == 0 {
            return Some(false);
        }
    }
}

struct ProcessHandle(HANDLE);

impl Drop for ProcessHandle {
    fn drop(&mut self) {
        unsafe { CloseHandle(self.0) };
    }
}

/// Classic LoadLibrary injection: write the path over, run LoadLibraryA on it.
/// kernel32 sits at the same address in every process in the session, so our
/// own LoadLibraryA pointer is valid over there.
fn inject(pid: u32, dll: &Path) -> Result<(), String> {
    let path = dll
        .to_str()
        .ok_or_else(|| "module path is not valid UTF-8".to_string())?;
    let path = CString::new(path).map_err(|_| "module path contains a NUL".to_string())?;
    let bytes = path.as_bytes_with_nul();

    let handle = unsafe {
        OpenProcess(
            PROCESS_CREATE_THREAD
                | PROCESS_QUERY_INFORMATION
                | PROCESS_VM_OPERATION
                | PROCESS_VM_WRITE
                | PROCESS_VM_READ,
            0,
            pid,
        )
    };
    if handle.is_null() {
        return Err(format!(
            "OpenProcess failed ({}). The game may be running elevated while the launcher is not.",
            std::io::Error::last_os_error()
        ));
    }
    let process = ProcessHandle(handle);

    let remote = unsafe {
        VirtualAllocEx(
            process.0,
            std::ptr::null(),
            bytes.len(),
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
        )
    };
    if remote.is_null() {
        return Err(format!("VirtualAllocEx failed ({})", std::io::Error::last_os_error()));
    }

    let mut written = 0usize;
    let ok = unsafe {
        windows_sys::Win32::System::Diagnostics::Debug::WriteProcessMemory(
            process.0,
            remote,
            bytes.as_ptr() as *const c_void,
            bytes.len(),
            &mut written,
        )
    };
    if ok == 0 || written != bytes.len() {
        unsafe { VirtualFreeEx(process.0, remote, 0, MEM_RELEASE) };
        return Err(format!("WriteProcessMemory failed ({})", std::io::Error::last_os_error()));
    }

    let kernel32 = unsafe { GetModuleHandleA(c"kernel32.dll".as_ptr() as *const u8) };
    if kernel32.is_null() {
        unsafe { VirtualFreeEx(process.0, remote, 0, MEM_RELEASE) };
        return Err("could not resolve kernel32".into());
    }
    let load_library = unsafe { GetProcAddress(kernel32, c"LoadLibraryA".as_ptr() as *const u8) };
    let Some(load_library) = load_library else {
        unsafe { VirtualFreeEx(process.0, remote, 0, MEM_RELEASE) };
        return Err("could not resolve LoadLibraryA".into());
    };

    let thread = unsafe {
        CreateRemoteThread(
            process.0,
            std::ptr::null(),
            0,
            Some(std::mem::transmute::<
                unsafe extern "system" fn() -> isize,
                unsafe extern "system" fn(*mut c_void) -> u32,
            >(load_library)),
            remote,
            0,
            std::ptr::null_mut(),
        )
    };
    if thread.is_null() {
        unsafe { VirtualFreeEx(process.0, remote, 0, MEM_RELEASE) };
        return Err(format!("CreateRemoteThread failed ({})", std::io::Error::last_os_error()));
    }

    // Waits on DllMain, which just spawns a thread and returns, so this is quick.
    unsafe {
        WaitForSingleObject(thread, INFINITE);
        CloseHandle(thread);
        VirtualFreeEx(process.0, remote, 0, MEM_RELEASE);
    }
    Ok(())
}

fn wait_for<T>(timeout: Duration, mut probe: impl FnMut() -> Option<T>) -> Option<T> {
    let deadline = Instant::now() + timeout;
    loop {
        if let Some(value) = probe() {
            return Some(value);
        }
        if Instant::now() >= deadline {
            return None;
        }
        thread::sleep(POLL_INTERVAL);
    }
}

/// Can't delete while it's loaded, the file stays locked. So sit and wait for
/// the module to eject itself (or the game to close).
fn delete_when_unloaded(pid: u32, dll: PathBuf) {
    let deadline = Instant::now() + WAIT_FOR_UNLOAD;
    loop {
        match has_module(pid, MODULE_FILE) {
            // gone, or the process is
            Some(false) | None => break,
            Some(true) => {}
        }
        if Instant::now() >= deadline {
            eprintln!("[inject] module still loaded after the cleanup window; leaving the file");
            return;
        }
        thread::sleep(Duration::from_secs(2));
    }
    match std::fs::remove_file(&dll) {
        Ok(()) => eprintln!("[inject] removed {}", dll.display()),
        Err(e) => eprintln!("[inject] could not remove {}: {e}", dll.display()),
    }
}

/// Fire-and-forget. Waits for the game, then injects.
///
/// Nothing in here is fatal. Worst case the player gets the server's stock UI,
/// which is not worth blocking a connect over, so every failure just logs.
pub(crate) fn spawn_inject_watch(app: &tauri::AppHandle) {
    if !is_enabled(app) {
        eprintln!("[inject] server custom UI is off in settings; not injecting");
        return;
    }

    thread::spawn(move || {
        let Some(pid) = wait_for(WAIT_FOR_PROCESS, || find_process(GAME_PROCESS)) else {
            eprintln!("[inject] {GAME_PROCESS} did not start within the timeout");
            return;
        };

        // Left over from an earlier connect this session. Only possible if the
        // reconnect beat the module's idle timeout.
        if has_module(pid, MODULE_FILE) == Some(true) {
            eprintln!("[inject] the client module is already loaded in pid {pid}");
            return;
        }

        let dll = module_path();
        match fetch_module(&dll) {
            Ok(version) if version.is_empty() => eprintln!("[inject] module verified"),
            Ok(version) => eprintln!("[inject] module {version} verified"),
            Err(e) => {
                eprintln!("[inject] {e}");
                return;
            }
        }

        if wait_for(WAIT_FOR_MODULE, || {
            has_module(pid, REQUIRED_MODULE).filter(|found| *found)
        })
        .is_none()
        {
            eprintln!("[inject] {REQUIRED_MODULE} never appeared in pid {pid}");
            let _ = std::fs::remove_file(&dll);
            return;
        }

        match inject(pid, &dll) {
            Ok(()) => {
                eprintln!("[inject] loaded the client module into pid {pid}");
                delete_when_unloaded(pid, dll);
            }
            Err(e) => {
                eprintln!("[inject] failed: {e}");
                let _ = std::fs::remove_file(&dll);
            }
        }
    });
}
