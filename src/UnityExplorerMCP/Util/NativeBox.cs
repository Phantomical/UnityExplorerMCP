using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace UnityExplorerMCP.Util;

[NativeContainerSupportsDeallocateOnJobCompletion]
internal unsafe struct NativeBox<T> : IDisposable
    where T : unmanaged
{
    private T* m_Buffer;
    private Allocator m_AllocatorLabel;

    public readonly bool IsCreated => m_Buffer is not null;
    public readonly Allocator Allocator => m_AllocatorLabel;
    public ref T Value => ref *m_Buffer;

    public NativeBox(Allocator allocator)
        : this(default, allocator) { }

    public NativeBox(T value, Allocator allocator)
    {
        m_Buffer = (T*)
            UnsafeUtility.Malloc(UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>(), allocator);
        m_AllocatorLabel = allocator;

        if (m_Buffer is null)
            ThrowOutOfMemory();

        *m_Buffer = value;
    }

    public void Dispose()
    {
        UnsafeUtility.Free(m_Buffer, m_AllocatorLabel);
        this = default;
    }

    struct DisposeJob(NativeBox<T> box) : IJob
    {
        NativeBox<T> box = box;

        public void Execute() => box.Dispose();
    }

    public void Dispose(JobHandle dependsOn)
    {
        new DisposeJob(this).Schedule(dependsOn);
        this = default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOutOfMemory() => throw new OutOfMemoryException();
}
