#define LOW_RESOLUTION

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

public class Sector : IDisposable
{


    #if LOW_RESOLUTION
        public const int CoreSizeInInputVoxels = 5; //number of voxels along every edge of the persisted grid. 
        public const int InputSizeInVoxels = 5; //need one more to interpolate output properly
        public const int OutputSizeInVoxels = 5;
        public const float InputVoxelSize = 2f;   //25cm per input voxel
        public const float OutputVoxelSize = 2f;   //25cm per input voxel
    #else

        public const int CoreSizeInInputVoxels = 32; //number of voxels along every edge of the persisted grid. 
        public const int InputSizeInVoxels = CoreSizeInInputVoxels + 1; //need one more to interpolate output properly
        public const float InputVoxelSize = 0.5f;   //50cm per input voxel
        public const int OutputMultiplier = 8;      //multiplier input -> output
        public const float OutputVoxelSize = InputVoxelSize / OutputMultiplier; //final output voxel (~5cm)
        public const int OutputSizeInVoxels = CoreSizeInInputVoxels * OutputMultiplier; //number of voxels along every edge in the final geometry

        //public const float VoxelSize = 0.1f;    //10cm per voxel
        public const float InputSectorSize = CoreSizeInInputVoxels * InputVoxelSize;
    #endif

    public const int KernelSize = 8;

    public const int TotalInputVoxelCount = InputSizeInVoxels * InputSizeInVoxels * InputSizeInVoxels;
    public const int TotalOutputVoxelCount = OutputSizeInVoxels * OutputSizeInVoxels * OutputSizeInVoxels;

    //public const int ByteCount = TotalVoxelCount * 2;

    public RenderTexture DensityMap { get; }
    public RenderTexture MaterialMap { get; }

    public Vector3 Origin { get; }

    public record Compacted(GraphicsBuffer VertexBuffer, GraphicsBuffer IndexBuffer, int NumVertex, int NumTriangles) : IDisposable
    {
        private volatile int _refCount = 1;

        public bool Reference()
        {
            while (true)
            {
                int r = _refCount;
                if (r <= 0)
                    return false;
                int r2 = Interlocked.CompareExchange(ref _refCount, r+1, r);
                if (r2 == r)
                    return true;
            }
        }

        public void Dereference()
        {
            var dec = Interlocked.Decrement(ref _refCount);
            if (dec == 0)
                Dispose();
        }

        public int RefCount => _refCount;

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }

        public static Vector3 V(float v) => new Vector3(v, v, v);

        public void Render(Sector parent, Material material)
        {
            RenderParams rp = new RenderParams(material);
            rp.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            rp.receiveShadows = true;
            rp.worldBounds = new Bounds(parent.Origin, parent.Origin + V(Sector.OutputVoxelSize * Sector.OutputSizeInVoxels)); // use tighter bounds
            rp.matProps = new MaterialPropertyBlock();
            rp.matProps.SetBuffer("_Positions", VertexBuffer);
            //rp.matProps.SetMatrix("_ObjectToWorld", Matrix4x4.Translate(parent.Origin));
            Graphics.RenderPrimitivesIndexed(rp, MeshTopology.Triangles, IndexBuffer, NumTriangles*3);
        }
    }


    private Compacted? Compact { get; set; }

    private SemaphoreSlim ReplaceLock { get; } = new(1);

    
    public async Task ReplaceCompactAsync(Compacted? replacement)
    {
        if (replacement is null || replacement.RefCount != 1)
            throw new ArgumentException("RefCount expected to be 1 at this point");
        Compacted? old;
        await ReplaceLock.WaitAsync();
        try
        {
            old = Compact;
            Compact = replacement;
        }
        finally
        {
            ReplaceLock.Release();
        }
        old?.Dereference();
    }

    public bool DoWithCompact(Action<Compacted> func)
    {
        while (true)
        {
            Compacted? c = Compact;
            if (c is null)
                return false;
            if (c.Reference())
                try
                {
                    func(c);
                    return true;
                }
                finally
                {
                    c.Dereference();
                }
        }
    }

    public Sector(Vector3 origin)
    {
        Origin = origin;
        DensityMap = ComputeOperator.MakeVolume(InputSizeInVoxels, RenderTextureFormat.R8);
        MaterialMap = ComputeOperator.MakeVolume(InputSizeInVoxels, RenderTextureFormat.R8);

    }

    public void Dispose()
    {
        UnityEngine.Object.Destroy(DensityMap);
        UnityEngine.Object.Destroy(MaterialMap);
        _ = ReplaceCompactAsync(null);
    }

    public void Render(Material material)
    {
        DoWithCompact(c => c.Render(this, material));
    }



    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
