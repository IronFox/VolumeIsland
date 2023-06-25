using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using static Sector;

#nullable enable

public static class ComputeOperator
{
    //private readonly ComputeBuffer counterValueBuffer;
    //private readonly ComputeBuffer threadGroupsBuffer;
    //public ComputeOperator()
    //{
    //    counterValueBuffer = new(1, 4, ComputeBufferType.Raw);
    //    threadGroupsBuffer = new(3, 4, ComputeBufferType.IndirectArguments);
    //}

    public static RenderTexture MakeVolume(int edgeDimension, RenderTextureFormat format)
    {
        RenderTexture t = new(edgeDimension, edgeDimension, 0, format);
        t.volumeDepth = edgeDimension;
        t.memorylessMode = RenderTextureMemoryless.None;
        t.dimension = TextureDimension.Tex3D;
        t.enableRandomWrite = true;
        t.Create();
        return t;
    }


    public static NativeArray<T> GetAllNativeNow<T>(ComputeBuffer source, int stride, int count, int offset = 0) where T : struct
    {
        if (count == 0)
            return new NativeArray<T>(0, Allocator.Temp, NativeArrayOptions.ClearMemory);
        var asyncRequest = AsyncGPUReadback.Request(source, stride * count, offset * count);
        asyncRequest.WaitForCompletion();

        return asyncRequest.GetData<T>();

    }

    public static Task<NativeArray<T>> GetAllNativeAsync<T>(ComputeBuffer source, int stride, int count, int offset = 0) where T : struct
    {
        if (count == 0)
            return Task.FromResult(
                new NativeArray<T>(0, Allocator.Temp, NativeArrayOptions.ClearMemory)
            );
        TaskCompletionSource<NativeArray<T>> rs = new();
        var asyncRequest = AsyncGPUReadback.Request(source, stride * count, offset * count, req =>
        {
            rs.SetResult(req.GetData<T>());
        });
        return rs.Task;
    }
    public static Task<NativeArray<T>> GetAllNativeAsync<T>(GraphicsBuffer source, int stride, int count, int offset=0) where T : struct
    {
        if (count == 0)
            return Task.FromResult(
                new NativeArray<T>(0, Allocator.Temp, NativeArrayOptions.ClearMemory)
            );
        TaskCompletionSource<NativeArray<T>> rs = new();
        var asyncRequest = AsyncGPUReadback.Request(source, stride * count, offset * count, req =>
        {
            rs.SetResult(req.GetData<T>());
        });
        return rs.Task;
    }


    public static T[] GetAllNow<T>(ComputeBuffer source, int stride, int count, int offset = 0) where T : struct
    {
        using var ar = GetAllNativeNow<T>(source, stride, count, offset);
        return ar.ToArray();
    }

    public static async Task<T[]> GetAllAsync<T>(ComputeBuffer source, int stride, int count, int offset=0) where T : struct
    {
        using var ar = await GetAllNativeAsync<T>(source, stride,count, offset);
        return ar.ToArray();
    }
    public static async Task<T[]> GetAllAsync<T>(GraphicsBuffer source, int stride, int count, int offset=0) where T : struct
    {
        using var ar = await GetAllNativeAsync<T>(source, stride, count, offset);
        return ar.ToArray();
    }


    public static int GetCountNow(ComputeBuffer source)
    {
        using var counter = new ComputeBuffer(1, 4, ComputeBufferType.Raw);
        ComputeBuffer.CopyCount(source, counter, 0);
        using var ar = GetAllNativeNow<int>(counter, counter.stride, 1, 0);
        return ar[0];
    }

    public static Task<int> GetCountAsync(ComputeBuffer source)
    {
        using var counter = new ComputeBuffer(1, 4, ComputeBufferType.Raw);
        TaskCompletionSource<int> rs = new();
        var t = Thread.CurrentThread;
        try
        {
            ComputeBuffer.CopyCount(source, counter, 0);
            var asyncRequest = AsyncGPUReadback.Request(counter, /*counterValueBuffer.stride*/4, 0, req =>
            {
                rs.SetResult(req.GetData<int>()[0]);
            });
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            return Task.FromResult(0);
        }
        return rs.Task;
    }

    public static void DispatchOversized(ComputeShader shader, int kernel, int threadGroupsX, int threadGroupsY = 1, int threadGroupsZ = 1)
    {
        using var threadGroupsBuffer = new ComputeBuffer(3, 4, ComputeBufferType.IndirectArguments);
        //shader.Dispatch(kernel, threadGroupsX, threadGroupsY, threadGroupsZ);
        threadGroupsBuffer.SetData(new int[] {threadGroupsX, threadGroupsY, threadGroupsZ});
        shader.DispatchIndirect(kernel, threadGroupsBuffer);

    }
}
