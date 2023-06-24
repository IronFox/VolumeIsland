using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class ComputeOperator : IDisposable
{
    private readonly ComputeBuffer counterValueBuffer;
    public ComputeOperator()
    {
        counterValueBuffer = new(1, 4, ComputeBufferType.Raw);
    }

    public static RenderTexture MakeVolume(int edgeDimension, RenderTextureFormat format)
    {
        RenderTexture t = new(edgeDimension, edgeDimension, 0, format);
        t.volumeDepth = edgeDimension;
        t.dimension = TextureDimension.Tex3D;
        t.enableRandomWrite = true;
        t.Create();
        return t;
    }


    public static NativeArray<T> GetAllNow<T>(ComputeBuffer source, int stride, int count, int offset) where T : struct
    {
        if (count == 0)
            return new NativeArray<T>(0, Allocator.Temp, NativeArrayOptions.ClearMemory);
        var asyncRequest = AsyncGPUReadback.Request(source, stride * count, offset * count);
        asyncRequest.WaitForCompletion();

        return asyncRequest.GetData<T>();

    }

    public static Task<NativeArray<T>> GetAllAsync<T>(ComputeBuffer source, int stride, int count, int offset) where T : struct
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


    public int GetNow(ComputeBuffer source)
    {
        ComputeBuffer.CopyCount(source, counterValueBuffer, 0);
        return GetAllNow<int>(counterValueBuffer, counterValueBuffer.stride, 1, 0)[0];
    }

    public Task<int> GetAsync(ComputeBuffer source)
    {
        TaskCompletionSource<int> rs = new();
        var t = Thread.CurrentThread;
        try
        {
            ComputeBuffer.CopyCount(source, counterValueBuffer, 0);
            var asyncRequest = AsyncGPUReadback.Request(counterValueBuffer, /*counterValueBuffer.stride*/4, 0, req =>
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

    public void Dispose()
    {
        counterValueBuffer.Dispose();
    }
}
