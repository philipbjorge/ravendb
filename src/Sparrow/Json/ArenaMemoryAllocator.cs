﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Sparrow.Binary;
using Sparrow.Logging;

namespace Sparrow.Json
{
    public unsafe class ArenaMemoryAllocator : IDisposable
    {
        private byte* _ptrStart;
        private byte* _ptrCurrent;

        private int _allocated;
        private int _used;

        private List<IntPtr> _olderBuffers;

        private bool _isDisposed;
        private static readonly Logger _logger = LoggingSource.Instance.GetLogger<ArenaMemoryAllocator>("ArenaMemoryAllocator");

        public int Allocated => _allocated;

        public ArenaMemoryAllocator(int initialSize = 1024 * 1024)
        {
            _ptrStart = _ptrCurrent = (byte*)Marshal.AllocHGlobal(initialSize).ToPointer();
            _allocated = initialSize;
            _used = 0;

            if (_logger.IsInfoEnabled)
                _logger.Info($"ArenaMemoryAllocator was created with initial capacity of {initialSize:#,#;;0} bytes");
        }

        public bool GrowAllocation(AllocatedMemoryData allocation, int sizeIncrease)
        {
            var end = (byte*)allocation.Address + allocation.SizeInBytes;
            var distance = end - _ptrCurrent;
            if (distance != 0)
                return false;

            if (_used + sizeIncrease > _allocated)
                return false;

            _ptrCurrent += sizeIncrease;
            _used += sizeIncrease;
            allocation.SizeInBytes += sizeIncrease;
            return true;
        }

        public AllocatedMemoryData Allocate(int size)
        {
            if (_isDisposed)
                ThrowAlreadyDisposedException();

            if (_used + size > _allocated)
                GrowArena(size);

            var allocation = new AllocatedMemoryData()
            {
                SizeInBytes = size,
                Address = new IntPtr(_ptrCurrent)
            };

            _ptrCurrent += size;
            _used += size;

            if (_logger.IsInfoEnabled)
                _logger.Info($"ArenaMemoryAllocator allocated {size:#,#;;0} bytes");
            return allocation;
        }

        private void ThrowAlreadyDisposedException()
        {
            throw new ObjectDisposedException("This ArenaMemoryAllocator is already disposed");
        }

        private void GrowArena(int requestedSize)
        {
            const int maxArenaSize = 1024*1024*1024;
            if (requestedSize >= maxArenaSize)
                throw new ArgumentOutOfRangeException(nameof(requestedSize));

            // we need the next allocation to cover at least the next expansion (also doubling)
            // so we'll allocate 3 times as much as was requested, or twice as much as we already have
            // the idea is that a single allocation can server for multiple (increasing in size) calls
            int newSize = Math.Max(Bits.NextPowerOf2(requestedSize)*3, _allocated * 2);
            if (newSize < 0 || newSize > maxArenaSize)
                newSize = maxArenaSize;
           

            if (_logger.IsInfoEnabled)
            {
                if (newSize > 512 * 1024 * 1024)
                    _logger.Info(
                        $"Arena main buffer reached size of {newSize:#,#;0} bytes (previously {_allocated:#,#;0} bytes), check if you forgot to reset the context. From now on we grow this arena in 1GB chunks.");
                _logger.Info(
                    $"Increased size of buffer from {_allocated:#,#;0} to {newSize:#,#;0} because we need {requestedSize:#,#;0}. _used={_used:#,#;0}");
            }

                
            var newBuffer = (byte*)Marshal.AllocHGlobal(newSize).ToPointer();
            _allocated = newSize;

            // Save the old buffer pointer to be released when the arena is reset
            if (_olderBuffers == null)
                _olderBuffers = new List<IntPtr>();
            _olderBuffers.Add(new IntPtr(_ptrStart));

            _ptrStart = newBuffer;
            _ptrCurrent = _ptrStart;
            _used = 0;
        }

        public void ResetArena()
        {
            // Reset current arena buffer
            _ptrCurrent = _ptrStart;
            _used = 0;

            // Free old buffers not being used anymore
            if (_olderBuffers != null)
            {
                foreach (var unusedBuffer in _olderBuffers)
                {
                    Marshal.FreeHGlobal(unusedBuffer);
                }
                _olderBuffers = null;
            }
        }

        ~ArenaMemoryAllocator()
        {
            if (_logger.IsInfoEnabled)
                _logger.Info("ArenaMemoryAllocator wasn't properly disposed");

            Dispose();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;
            _isDisposed = true;

            ResetArena();

            Marshal.FreeHGlobal(new IntPtr(_ptrStart));

            GC.SuppressFinalize(this);
        }

        public void Return(AllocatedMemoryData allocation)
        {
            if ((byte*)allocation.Address != _ptrCurrent - allocation.SizeInBytes ||
                (byte*)allocation.Address < _ptrStart)
                return;
            // since the returned allocation is at the end of the arena, we can just move
            // the pointer back
            _used -= allocation.SizeInBytes;
            _ptrCurrent -= allocation.SizeInBytes;
        }
    }

    public class AllocatedMemoryData
    {
        public IntPtr Address;
        public int SizeInBytes;
    }
}