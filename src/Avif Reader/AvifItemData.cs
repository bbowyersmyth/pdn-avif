﻿////////////////////////////////////////////////////////////////////////
//
// This file is part of pdn-avif, a FileType plugin for Paint.NET
// that loads and saves AVIF images.
//
// Copyright (c) 2020 Nicholas Hayes
//
// This file is licensed under the MIT License.
// See LICENSE.txt for complete licensing and attribution information.
//
////////////////////////////////////////////////////////////////////////

using AvifFileType.Interop;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AvifFileType
{
    internal unsafe delegate void UseBufferPointerDelegate(byte* ptr, ulong length);

    internal abstract class AvifItemData
        : IDisposable
    {
        private bool disposed;

        protected AvifItemData()
        {
            this.disposed = false;
        }

        public ulong Length { get; protected set; }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public Stream GetStream()
        {
            VerifyNotDisposed();

            return GetStreamImpl();
        }

        public byte[] ToArray()
        {
            VerifyNotDisposed();

            return ToArrayImpl();
        }

        public unsafe void UseBufferPointer(UseBufferPointerDelegate action)
        {
            if (action is null)
            {
                ExceptionUtil.ThrowArgumentNullException(nameof(action));
            }

            VerifyNotDisposed();

            UseBufferPointerImpl(action);
        }

        protected virtual void Dispose(bool disposing)
        {
            this.disposed = true;
        }

        protected abstract Stream GetStreamImpl();

        protected abstract byte[] ToArrayImpl();

        protected abstract unsafe void UseBufferPointerImpl(UseBufferPointerDelegate action);

        protected void VerifyNotDisposed()
        {
            if (this.disposed)
            {
                ExceptionUtil.ThrowObjectDisposedException(GetType().Name);
            }
        }
    }

    internal sealed class ManagedAvifItemData
        : AvifItemData
    {
        private byte[] buffer;
        private readonly IByteArrayPool arrayPool;

        public ManagedAvifItemData(int length, IByteArrayPool pool)
            : base()
        {
            this.buffer = pool.Rent(length);
            this.Length = (ulong)length;
            this.arrayPool = pool;
        }

        internal byte[] GetBuffer()
        {
            VerifyNotDisposed();

            return this.buffer;
        }

        protected override void Dispose(bool disposing)
        {
            this.arrayPool.Return(this.buffer);
            this.buffer = null;

            base.Dispose(disposing);
        }

        protected override Stream GetStreamImpl()
        {
            return new MemoryStream(this.buffer, 0, (int)this.Length, writable: false);
        }

        protected override byte[] ToArrayImpl()
        {
            byte[] array = new byte[this.Length];
            this.buffer.CopyTo(array, 0);

            return array;
        }

        protected override unsafe void UseBufferPointerImpl(UseBufferPointerDelegate action)
        {
            fixed (byte* ptr = this.buffer)
            {
                action(ptr, this.Length);
            }
        }
    }

    internal sealed class UnmanagedAvifItemData
        : AvifItemData
    {
        private SafeProcessHeapBuffer buffer;

        public UnmanagedAvifItemData(ulong length)
            : base()
        {
            this.buffer = SafeProcessHeapBuffer.Create(length);
            this.Length = length;
        }

        public SafeBuffer UnmanagedBuffer
        {
            get
            {
                VerifyNotDisposed();

                return this.buffer;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.buffer != null)
                {
                    this.buffer.Dispose();
                    this.buffer = null;
                }
            }

            base.Dispose(disposing);
        }

        protected override Stream GetStreamImpl()
        {
            // The UnmanagedMemoryStream class does not take ownership of the SafeBuffer.
            return new UnmanagedMemoryStream(this.buffer, 0, checked((long)this.Length), FileAccess.Read);
        }

        protected override unsafe byte[] ToArrayImpl()
        {
            ulong length = this.Length;

            byte[] array = new byte[length];

            byte* readPtr = null;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                this.buffer.AcquirePointer(ref readPtr);

                fixed (byte* writePtr = array)
                {
                    Buffer.MemoryCopy(readPtr, writePtr, length, length);
                }
            }
            finally
            {
                if (readPtr != null)
                {
                    this.buffer.ReleasePointer();
                }
            }

            return array;
        }

        protected override unsafe void UseBufferPointerImpl(UseBufferPointerDelegate action)
        {
            byte* ptr = null;
            RuntimeHelpers.PrepareDelegate(action);
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                this.buffer.AcquirePointer(ref ptr);

                action(ptr, this.Length);
            }
            finally
            {
                if (ptr != null)
                {
                    this.buffer.ReleasePointer();
                }
            }
        }
    }
}
