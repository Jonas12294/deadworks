using System.Numerics;

namespace DeadworksManaged.Api;

/// <summary>
/// Wraps a native KeyValues3 handle. Create with <c>new KeyValues3()</c>,
/// set typed members, pass to <see cref="CBaseEntity.AddModifier"/>, then dispose.
/// Must be disposed explicitly — no finalizer (unsafe to call native from GC thread).
/// </summary>
public sealed unsafe class KeyValues3 : IDisposable
{
	internal nint Handle { get; private set; }

	/// <summary><see langword="true"/> if the underlying native handle is still alive (i.e. not yet disposed).</summary>
	public bool IsValid => Handle != 0;

	/// <summary>Allocates a new native KeyValues3 object.</summary>
	public KeyValues3()
	{
		Handle = (nint)NativeInterop.KV3Create();
		_owned = true;
	}

	/// <summary>Wraps an existing native KeyValues3 pointer without taking ownership.</summary>
	internal KeyValues3(nint handle)
	{
		Handle = handle;
		_owned = false;
	}

	private readonly bool _owned;

	/// <summary>Frees the native KeyValues3 object. Must be called after the KV3 has been consumed by the engine.</summary>
	public void Dispose()
	{
		if (Handle != 0 && _owned)
		{
			NativeInterop.KV3Destroy((void*)Handle);
			Handle = 0;
		}
	}

	/// <summary>Sets a string value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The string value.</param>
	public void SetString(string key, string value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		Span<byte> valUtf8 = Utf8.Encode(value, stackalloc byte[Utf8.Size(value)]);
		fixed (byte* keyPtr = keyUtf8, valPtr = valUtf8)
		{
			NativeInterop.KV3SetString((void*)Handle, keyPtr, valPtr);
		}
	}

	/// <summary>Sets a boolean value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetBool(string key, bool value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetBool((void*)Handle, keyPtr, value ? (byte)1 : (byte)0);
		}
	}

	/// <summary>Sets a signed 32-bit integer value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetInt(string key, int value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetInt((void*)Handle, keyPtr, value);
		}
	}

	/// <summary>Sets an unsigned 32-bit integer value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetUInt(string key, uint value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetUInt((void*)Handle, keyPtr, value);
		}
	}

	/// <summary>Sets a signed 64-bit integer value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetInt64(string key, long value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetInt64((void*)Handle, keyPtr, value);
		}
	}

	/// <summary>Sets an unsigned 64-bit integer value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetUInt64(string key, ulong value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetUInt64((void*)Handle, keyPtr, value);
		}
	}

	/// <summary>Sets a single-precision floating-point value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetFloat(string key, float value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetFloat((void*)Handle, keyPtr, value);
		}
	}

	/// <summary>Sets a double-precision floating-point value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The value.</param>
	public void SetDouble(string key, double value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetDouble((void*)Handle, keyPtr, value);
		}
	}

	/// <summary>Sets a 3D vector value on the KV3 object.</summary>
	/// <param name="key">The key name.</param>
	/// <param name="value">The vector value.</param>
	public void SetVector(string key, Vector3 value)
	{
		ThrowIfDisposed();
		Span<byte> keyUtf8 = Utf8.Encode(key, stackalloc byte[Utf8.Size(key)]);
		fixed (byte* keyPtr = keyUtf8)
		{
			NativeInterop.KV3SetVector((void*)Handle, keyPtr, value.X, value.Y, value.Z);
		}
	}

	private void ThrowIfDisposed()
	{
		if (Handle == 0)
			throw new ObjectDisposedException(nameof(KeyValues3));
	}
}
