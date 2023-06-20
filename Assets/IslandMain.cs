//#define VERTEX_ONLY

#define LOW_RESOLUTION

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;
using Color = UnityEngine.Color;

public class IslandMain : MonoBehaviour
{
    //public ComputeShader shader;
    public ComputeShader generateTerrain;
    public ComputeShader upscaleTerrain;
    public ComputeShader emitVertexes;
    public ComputeShader marchingCubes;
    public RenderTexture outTarget;

    public GameObject slicePrefab;

    public ComputeBuffer SharedTriangleTable { get; private set; }
    public ComputeBuffer SharedJobSizeBuffer { get; private set; }
    public ComputeBuffer SharedVertexBuffer { get; private set; }
    public ComputeBuffer SharedIndexBuffer { get; private set; }

#if !LOW_RESOLUTION
    public RenderTexture SharedUpscaledDensityMap { get; private set;  }
#endif

#if !VERTEX_ONLY

    public RenderTexture SharedVertexIndexMapX { get; private set; }
    public RenderTexture SharedVertexIndexMapY { get; private set; }
    public RenderTexture SharedVertexIndexMapZ { get; private set; }
    public ComputeBuffer SharedCellBuffer { get; private set; }
    public ComputeBuffer DebugIndexOutX { get; private set; }
    public ComputeBuffer DebugIndexOutY { get; private set; }
    public ComputeBuffer DebugIndexOutZ { get; private set; }

#endif

    public class VertexLayout
    {
        public const int PositionSizeFloats = 3;
        public const int NormalSizeFloats = 3;


        public const int SizeFloats = PositionSizeFloats + NormalSizeFloats;

        public const int SizeBytes = SizeFloats * 4;
    }



    public class CountResolver : IDisposable
    {
        private readonly ComputeBuffer counterValueBuffer;
        public CountResolver()
        {

            counterValueBuffer = new(1, 4, ComputeBufferType.Raw);


            //Debug.Log("count: " + count);


            //int[] data = new int[count * 3];
            //counterBuffer.GetData(data);


            //countResult = new Vector3Int[count];
            //for (int i = 0; i < count; i++)
            //    countResult[i] = new(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);


        }

        public static NativeArray<T> GetAllNow<T>(ComputeBuffer source, int stride, int count, int offset) where T:struct
        {
            if (count == 0)
                return new NativeArray<T>(0,Allocator.Temp, NativeArrayOptions.ClearMemory);
            var asyncRequest = AsyncGPUReadback.Request(source, stride*count, offset * count);
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
            var asyncRequest = AsyncGPUReadback.Request(source, stride*count, offset*count, req =>
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
            ComputeBuffer.CopyCount(source, counterValueBuffer, 0);
            TaskCompletionSource<int> rs = new();
            var asyncRequest = AsyncGPUReadback.Request(counterValueBuffer, counterValueBuffer.stride, 0,req =>
            {
                rs.SetResult(req.GetData<int>()[0]);
            });
            return rs.Task;
        }

        public void Dispose()
        {
            counterValueBuffer.Dispose();
        }
    }


    public class Sector : IDisposable
    {
#if LOW_RESOLUTION
        public const int CoreSizeInInputVoxels = 3; //number of voxels along every edge of the persisted grid. 
        public const int InputSizeInVoxels = 3; //need one more to interpolate output properly
        public const int OutputSizeInVoxels = 3;
        public const float InputVoxelSize = 0.5f;   //50cm per input voxel
        public const float OutputVoxelSize = 0.5f;   //50cm per input voxel
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




        //public byte[] DensityMaterial = new byte[ByteCount];
        //public Texture3D? Downloaded { get; private set; }


        //public int GetAddr(int x, int y, int z) => (x* SizeInVoxels  + y)* SizeInVoxels + z;


        //public float GetDensity(int x, int y, int z)
        //{
        //    return DensityMaterial[GetAddr(x, y, z)*2] / 255f;
        //}
        //public int GetMaterial(int x, int y, int z)
        //{
        //    return DensityMaterial[GetAddr(x, y, z)*2+1];
        //}



        public Sector(Vector3 origin)
        {
            Origin = origin;
            DensityMap = MakeVolume(InputSizeInVoxels, RenderTextureFormat.R8);
            MaterialMap = MakeVolume(InputSizeInVoxels, RenderTextureFormat.R8);

        }

        public void Dispose()
        {
            Destroy(DensityMap);
            Destroy(MaterialMap);
        }
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

    public Sector sector;


    void OnDestroy()
    {
        sector?.Dispose();
        Destroy(SharedVertexIndexMapX);
        Destroy(SharedVertexIndexMapY);
        Destroy(SharedVertexIndexMapZ);
        SharedVertexBuffer?.Dispose();
        SharedIndexBuffer?.Dispose();
        SharedCellBuffer?.Dispose();
        DebugIndexOutX?.Dispose();
        DebugIndexOutY?.Dispose();
        DebugIndexOutZ?.Dispose();
#if !LOW_RESOLUTION
        Destroy(SharedUpscaledDensityMap);
#endif
        SharedTriangleTable?.Dispose();
        SharedJobSizeBuffer?.Dispose();
    }

    private static void Dispatch(ComputeShader shader, int kernel, int resolution)
    {
        int x = (resolution+Sector.KernelSize -1) / Sector.KernelSize;
        shader.Dispatch(kernel, x,x,x);

    }

    private static Vector3 V(float x) => new(x, x, x);
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private static void DebugDraw(Vector3 v0, Vector3 v1, Color c)
    {
        Debug.DrawLine(v0*10-V(5,5,0), v1 * 10 - V(5,5,0), c, 1e20f);
    }

    private static void DebugDrawPoint(float x, float y, float z, Color c, float size = 0.1f)
    {
        DebugDraw(new(x - size, y, z), new(x + size, y, z), c);
        DebugDraw(new(x, y - size, z), new(x, y + size, z), c);
        DebugDraw(new(x, y, z - size), new(x, y, z + size), c);

    }

    private static string Ar<T>(IEnumerable<T> i) => "["+string.Join(",", i)+"]";

    public float[] outVertexData;
    public int[] outIndexData;

    // Start is called before the first frame update
    void Start()
    {
#if !LOW_RESOLUTION
        SharedUpscaledDensityMap = MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.R16);
#endif
        SharedJobSizeBuffer = new(3, 4, ComputeBufferType.IndirectArguments);
        SharedJobSizeBuffer.SetData(new int[] { 1, 1, 1 });

        SharedTriangleTable = new(TriTable.Length, 2, ComputeBufferType.Constant);
        SharedTriangleTable.SetData(new NativeArray<short>(TriTable,Allocator.Temp));


        sector = new(Vector3.zero);

        SharedVertexIndexMapX = MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);
        SharedVertexIndexMapY = MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);
        SharedVertexIndexMapZ = MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);


        SharedIndexBuffer = new(Sector.TotalOutputVoxelCount * 5, 12, ComputeBufferType.Append);
        SharedVertexBuffer = new(Sector.TotalOutputVoxelCount * 3, VertexLayout.SizeBytes, ComputeBufferType.Counter);
        Debug.Log($"Vertex buffer accomodates {SharedVertexBuffer.count} vertexes in {SharedVertexBuffer.count * SharedVertexBuffer.stride} bytes");
        SharedVertexBuffer.SetCounterValue(0);

        SharedCellBuffer = new(Sector.TotalOutputVoxelCount, 16, ComputeBufferType.Append); //x,y,z,i = 3*4
        SharedCellBuffer.SetCounterValue(0);

        DebugIndexOutX = new(Sector.TotalOutputVoxelCount, 16, ComputeBufferType.Append); //x,y,z,i = 3*4
        DebugIndexOutX.SetCounterValue(0);
        DebugIndexOutY = new(Sector.TotalOutputVoxelCount, 16, ComputeBufferType.Append); //x,y,z,i = 3*4
        DebugIndexOutY.SetCounterValue(0);
        DebugIndexOutZ = new(Sector.TotalOutputVoxelCount, 16, ComputeBufferType.Append); //x,y,z,i = 3*4
        DebugIndexOutZ.SetCounterValue(0);





        var kernel = generateTerrain.FindKernel("Main");
        generateTerrain.SetTexture(kernel, "DensityVolume", sector.DensityMap);
        generateTerrain.SetTexture(kernel, "MaterialVolume", sector.MaterialMap);
        generateTerrain.SetVector("Origin", sector.Origin);
        generateTerrain.SetFloat("VoxelSize", Sector.InputVoxelSize);
        generateTerrain.SetInt("SizeInVoxels", Sector.InputSizeInVoxels);

        //using ComputeBuffer counterBuffer = new ComputeBuffer(65536, sizeof(int) * 3, ComputeBufferType.Append);
        //counterBuffer.SetCounterValue(0);
        //generateTerrain.SetBuffer(kernel, "TestBuffer", counterBuffer);

        Dispatch(generateTerrain, kernel, Sector.InputSizeInVoxels);

#if !LOW_RESOLUTION
        kernel = upscaleTerrain.FindKernel("Upscale");
        upscaleTerrain.SetTexture(kernel, "Volume", sector.DensityMaterialMap);
        upscaleTerrain.SetVector("Origin", sector.Origin);
        upscaleTerrain.SetInt("InputVoxelResolution", Sector.InputSizeInVoxels);
        upscaleTerrain.SetInt("OutputVoxelResolution", Sector.OutputSizeInVoxels);
        upscaleTerrain.SetInt("InputOutputMultiplier", Sector.OutputMultiplier);
        upscaleTerrain.SetTexture(kernel, "OutputDensity", SharedUpscaledDensityMap);

        Dispatch(upscaleTerrain, kernel, Sector.OutputSizeInVoxels);
        outTarget = SharedUpscaledDensityMap;
#endif





        kernel = emitVertexes.FindKernel("EmitVertex");
        SharedVertexBuffer.SetCounterValue(0);
#if !LOW_RESOLUTION
        emitVertexes.SetTexture(kernel, "Volume", SharedUpscaledDensityMap);
#else
        emitVertexes.SetTexture(kernel, "Volume", sector.DensityMap);
#endif
        emitVertexes.SetVector("Origin", sector.Origin);
        emitVertexes.SetFloat("VoxelSize", Sector.OutputVoxelSize);
        emitVertexes.SetInt("SizeInVoxels", Sector.OutputSizeInVoxels);
        emitVertexes.SetBuffer(kernel, "VertexOut", SharedVertexBuffer);
        emitVertexes.SetBuffer(kernel, "CellOut", SharedCellBuffer);
        emitVertexes.SetTexture(kernel,"IndexOutMapX" , SharedVertexIndexMapX);
        emitVertexes.SetTexture(kernel,"IndexOutMapY" , SharedVertexIndexMapY);
        emitVertexes.SetTexture(kernel,"IndexOutMapZ" , SharedVertexIndexMapZ);
        emitVertexes.SetBuffer(kernel,"DebugIndexOutX" , DebugIndexOutX);
        emitVertexes.SetBuffer(kernel,"DebugIndexOutY" , DebugIndexOutY);
        emitVertexes.SetBuffer(kernel,"DebugIndexOutZ" , DebugIndexOutZ);
        
        Dispatch(emitVertexes, kernel, Sector.OutputSizeInVoxels);

        using CountResolver resolver = new();
        var vertexCount = resolver.GetNow(SharedVertexBuffer);
        Debug.Log("Emitted vertexes: " + vertexCount);



        Debug.Log("Emitted cells: " + resolver.GetNow(SharedCellBuffer));
        int[] cells = CountResolver.GetAllNow<int>(SharedCellBuffer, 4, 4, 0).ToArray();
        Debug.Log($"First Cell: {string.Join(", ", cells)}");
        Debug.Log($"Bits: {cells[3].ToBinaryString()}");

        var requestBytes = VertexLayout.SizeBytes * vertexCount;
        Debug.Log("Requesting " + requestBytes + " bytes");

        outVertexData = CountResolver.GetAllNow<float>(SharedVertexBuffer, VertexLayout.SizeBytes, vertexCount, 0).ToArray();
        Debug.Log($"Got {outVertexData.Length} vertexes from VRAM");

        int xCnt = resolver.GetNow(DebugIndexOutX);
        int yCnt = resolver.GetNow(DebugIndexOutY);
        int zCnt = resolver.GetNow(DebugIndexOutZ);

        Debug.Log($"Got {xCnt},{yCnt},{zCnt} indexes");

        int[] debugIndexOutX = CountResolver.GetAllNow<int>(DebugIndexOutX, 4, xCnt * 4, 0).ToArray();
        int[] debugIndexOutY = CountResolver.GetAllNow<int>(DebugIndexOutY, 4, yCnt * 4, 0).ToArray();
        int[] debugIndexOutZ = CountResolver.GetAllNow<int>(DebugIndexOutZ, 4, zCnt * 4, 0).ToArray();

        Debug.Log($"X:{Ar(debugIndexOutX)}");
        Debug.Log($"Y:{Ar(debugIndexOutY)}");
        Debug.Log($"Z:{Ar(debugIndexOutZ)}");




        for (int i = 0; i < Sector.OutputSizeInVoxels; i++)
        for (int j = 0; j < Sector.OutputSizeInVoxels; j++)
        for (int k = 0; k < Sector.OutputSizeInVoxels; k++)
        {
            DebugDraw(
                sector.Origin + new Vector3(i, j, 0) * Sector.OutputVoxelSize, 
                sector.Origin + new Vector3(i, j, Sector.CoreSizeInInputVoxels) * Sector.OutputVoxelSize,
                Color.gray);
            DebugDraw(
                sector.Origin + new Vector3(i, 0, j) * Sector.OutputVoxelSize, 
                sector.Origin + new Vector3(i, Sector.CoreSizeInInputVoxels, j) * Sector.OutputVoxelSize,
                Color.gray);
            DebugDraw(
                sector.Origin + new Vector3(0, i, j) * Sector.OutputVoxelSize, 
                sector.Origin + new Vector3(Sector.CoreSizeInInputVoxels, i, j) * Sector.OutputVoxelSize,
                Color.gray);
        }
        for (int i = 0; i < vertexCount; i++)
        {
            float x = outVertexData[i * VertexLayout.SizeFloats];
            float y = outVertexData[i * VertexLayout.SizeFloats + 1];
            float z = outVertexData[i * VertexLayout.SizeFloats + 2];
            DebugDrawPoint(x, y, z, UnityEngine.Color.red);
        }

        
        //Texture2D t = new Texture2D(SharedVertexIndexMapX.width, SharedVertexIndexMapX.height, TextureFormat.RFloat, false);

        //Graphics.CopyTexture(SharedVertexIndexMapX, t);
        //Debug.Log($"Z0: {Ar(t.GetPixelData<int>(0))}");


        ComputeBuffer.CopyCount(SharedCellBuffer, SharedJobSizeBuffer, 0);

        var cntReq = AsyncGPUReadback.Request(SharedJobSizeBuffer, 12, 0);
        cntReq.WaitForCompletion();
        Debug.Log($"Size: {cntReq.GetData<uint>()[0]},{cntReq.GetData<uint>()[1]},{cntReq.GetData<uint>()[2]}");


        kernel = marchingCubes.FindKernel("EmitTriangles");
        SharedIndexBuffer.SetCounterValue(0);
        marchingCubes.SetBuffer(kernel, "TriTable", SharedTriangleTable);
        marchingCubes.SetBuffer(kernel, "CellTable", SharedCellBuffer);
        marchingCubes.SetBuffer(kernel, "IndexOutBuffer", SharedIndexBuffer);
        marchingCubes.SetTexture(kernel, "IndexInMapX", SharedVertexIndexMapX);
        marchingCubes.SetTexture(kernel, "IndexInMapY", SharedVertexIndexMapY);
        marchingCubes.SetTexture(kernel, "IndexInMapZ", SharedVertexIndexMapZ);
        marchingCubes.DispatchIndirect(kernel, SharedJobSizeBuffer);


        var numT = resolver.GetNow(SharedIndexBuffer);
        Debug.Log($"Emitted index triples: {numT}");

        Debug.Log($"Indexes: {Ar(CountResolver.GetAllNow<int>(SharedIndexBuffer,4,numT*3,0))}");
        //var idxReq = AsyncGPUReadback.Request(SharedIndexBuffer, 4 * 3 * Math.Min(10,numT*3), 0);
        //idxReq.WaitForCompletion();
        //outIndexData = idxReq.GetData<int>().ToArray();

        if (slicePrefab is not null)
        {
            for (int i = 0; i < Sector.OutputSizeInVoxels; i++)
            {
                var slice = Instantiate(slicePrefab, transform);
                slice.transform.localScale = new Vector3(10, 10, 10);
                slice.transform.localPosition = new Vector3(0, 0, (float)i / (Sector.OutputSizeInVoxels-1) * 10);
                var mesh = slice.GetComponentInChildren<MeshRenderer>();
                if (mesh is not null)
                {
                    mesh.material.SetFloat("_Slice", (float)i / Sector.OutputSizeInVoxels);
                    mesh.material.SetFloat("_SizeInVoxels", Sector.OutputSizeInVoxels);
#if !LOW_RESOLUTION
                    mesh.material.SetTexture("_MainTex", SharedUpscaledDensityMap);
#else
                    mesh.material.SetTexture("_MainTex", sector.DensityMap);

#endif
                }


            }
        }

    }

    private static readonly short[] TriTable = {
-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 8, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 1, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 8, 3, 9, 8, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 2, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 8, 3, 1, 2, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
9, 2, 10, 0, 2, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
2, 8, 3, 2, 10, 8, 10, 9, 8, -1, -1, -1, -1, -1, -1, -1,
3, 11, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 11, 2, 8, 11, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 9, 0, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 11, 2, 1, 9, 11, 9, 8, 11, -1, -1, -1, -1, -1, -1, -1,
3, 10, 1, 11, 10, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 10, 1, 0, 8, 10, 8, 11, 10, -1, -1, -1, -1, -1, -1, -1,
3, 9, 0, 3, 11, 9, 11, 10, 9, -1, -1, -1, -1, -1, -1, -1,
9, 8, 10, 10, 8, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 7, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 3, 0, 7, 3, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 1, 9, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 1, 9, 4, 7, 1, 7, 3, 1, -1, -1, -1, -1, -1, -1, -1,
1, 2, 10, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
3, 4, 7, 3, 0, 4, 1, 2, 10, -1, -1, -1, -1, -1, -1, -1,
9, 2, 10, 9, 0, 2, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1,
2, 10, 9, 2, 9, 7, 2, 7, 3, 7, 9, 4, -1, -1, -1, -1,
8, 4, 7, 3, 11, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
11, 4, 7, 11, 2, 4, 2, 0, 4, -1, -1, -1, -1, -1, -1, -1,
9, 0, 1, 8, 4, 7, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1,
4, 7, 11, 9, 4, 11, 9, 11, 2, 9, 2, 1, -1, -1, -1, -1,
3, 10, 1, 3, 11, 10, 7, 8, 4, -1, -1, -1, -1, -1, -1, -1,
1, 11, 10, 1, 4, 11, 1, 0, 4, 7, 11, 4, -1, -1, -1, -1,
4, 7, 8, 9, 0, 11, 9, 11, 10, 11, 0, 3, -1, -1, -1, -1,
4, 7, 11, 4, 11, 9, 9, 11, 10, -1, -1, -1, -1, -1, -1, -1,
9, 5, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
9, 5, 4, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 5, 4, 1, 5, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
8, 5, 4, 8, 3, 5, 3, 1, 5, -1, -1, -1, -1, -1, -1, -1,
1, 2, 10, 9, 5, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
3, 0, 8, 1, 2, 10, 4, 9, 5, -1, -1, -1, -1, -1, -1, -1,
5, 2, 10, 5, 4, 2, 4, 0, 2, -1, -1, -1, -1, -1, -1, -1,
2, 10, 5, 3, 2, 5, 3, 5, 4, 3, 4, 8, -1, -1, -1, -1,
9, 5, 4, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 11, 2, 0, 8, 11, 4, 9, 5, -1, -1, -1, -1, -1, -1, -1,
0, 5, 4, 0, 1, 5, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1,
2, 1, 5, 2, 5, 8, 2, 8, 11, 4, 8, 5, -1, -1, -1, -1,
10, 3, 11, 10, 1, 3, 9, 5, 4, -1, -1, -1, -1, -1, -1, -1,
4, 9, 5, 0, 8, 1, 8, 10, 1, 8, 11, 10, -1, -1, -1, -1,
5, 4, 0, 5, 0, 11, 5, 11, 10, 11, 0, 3, -1, -1, -1, -1,
5, 4, 8, 5, 8, 10, 10, 8, 11, -1, -1, -1, -1, -1, -1, -1,
9, 7, 8, 5, 7, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
9, 3, 0, 9, 5, 3, 5, 7, 3, -1, -1, -1, -1, -1, -1, -1,
0, 7, 8, 0, 1, 7, 1, 5, 7, -1, -1, -1, -1, -1, -1, -1,
1, 5, 3, 3, 5, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
9, 7, 8, 9, 5, 7, 10, 1, 2, -1, -1, -1, -1, -1, -1, -1,
10, 1, 2, 9, 5, 0, 5, 3, 0, 5, 7, 3, -1, -1, -1, -1,
8, 0, 2, 8, 2, 5, 8, 5, 7, 10, 5, 2, -1, -1, -1, -1,
2, 10, 5, 2, 5, 3, 3, 5, 7, -1, -1, -1, -1, -1, -1, -1,
7, 9, 5, 7, 8, 9, 3, 11, 2, -1, -1, -1, -1, -1, -1, -1,
9, 5, 7, 9, 7, 2, 9, 2, 0, 2, 7, 11, -1, -1, -1, -1,
2, 3, 11, 0, 1, 8, 1, 7, 8, 1, 5, 7, -1, -1, -1, -1,
11, 2, 1, 11, 1, 7, 7, 1, 5, -1, -1, -1, -1, -1, -1, -1,
9, 5, 8, 8, 5, 7, 10, 1, 3, 10, 3, 11, -1, -1, -1, -1,
5, 7, 0, 5, 0, 9, 7, 11, 0, 1, 0, 10, 11, 10, 0, -1,
11, 10, 0, 11, 0, 3, 10, 5, 0, 8, 0, 7, 5, 7, 0, -1,
11, 10, 5, 7, 11, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
10, 6, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 8, 3, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
9, 0, 1, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 8, 3, 1, 9, 8, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1,
1, 6, 5, 2, 6, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 6, 5, 1, 2, 6, 3, 0, 8, -1, -1, -1, -1, -1, -1, -1,
9, 6, 5, 9, 0, 6, 0, 2, 6, -1, -1, -1, -1, -1, -1, -1,
5, 9, 8, 5, 8, 2, 5, 2, 6, 3, 2, 8, -1, -1, -1, -1,
2, 3, 11, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
11, 0, 8, 11, 2, 0, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1,
0, 1, 9, 2, 3, 11, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1,
5, 10, 6, 1, 9, 2, 9, 11, 2, 9, 8, 11, -1, -1, -1, -1,
6, 3, 11, 6, 5, 3, 5, 1, 3, -1, -1, -1, -1, -1, -1, -1,
0, 8, 11, 0, 11, 5, 0, 5, 1, 5, 11, 6, -1, -1, -1, -1,
3, 11, 6, 0, 3, 6, 0, 6, 5, 0, 5, 9, -1, -1, -1, -1,
6, 5, 9, 6, 9, 11, 11, 9, 8, -1, -1, -1, -1, -1, -1, -1,
5, 10, 6, 4, 7, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 3, 0, 4, 7, 3, 6, 5, 10, -1, -1, -1, -1, -1, -1, -1,
1, 9, 0, 5, 10, 6, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1,
10, 6, 5, 1, 9, 7, 1, 7, 3, 7, 9, 4, -1, -1, -1, -1,
6, 1, 2, 6, 5, 1, 4, 7, 8, -1, -1, -1, -1, -1, -1, -1,
1, 2, 5, 5, 2, 6, 3, 0, 4, 3, 4, 7, -1, -1, -1, -1,
8, 4, 7, 9, 0, 5, 0, 6, 5, 0, 2, 6, -1, -1, -1, -1,
7, 3, 9, 7, 9, 4, 3, 2, 9, 5, 9, 6, 2, 6, 9, -1,
3, 11, 2, 7, 8, 4, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1,
5, 10, 6, 4, 7, 2, 4, 2, 0, 2, 7, 11, -1, -1, -1, -1,
0, 1, 9, 4, 7, 8, 2, 3, 11, 5, 10, 6, -1, -1, -1, -1,
9, 2, 1, 9, 11, 2, 9, 4, 11, 7, 11, 4, 5, 10, 6, -1,
8, 4, 7, 3, 11, 5, 3, 5, 1, 5, 11, 6, -1, -1, -1, -1,
5, 1, 11, 5, 11, 6, 1, 0, 11, 7, 11, 4, 0, 4, 11, -1,
0, 5, 9, 0, 6, 5, 0, 3, 6, 11, 6, 3, 8, 4, 7, -1,
6, 5, 9, 6, 9, 11, 4, 7, 9, 7, 11, 9, -1, -1, -1, -1,
10, 4, 9, 6, 4, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 10, 6, 4, 9, 10, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1,
10, 0, 1, 10, 6, 0, 6, 4, 0, -1, -1, -1, -1, -1, -1, -1,
8, 3, 1, 8, 1, 6, 8, 6, 4, 6, 1, 10, -1, -1, -1, -1,
1, 4, 9, 1, 2, 4, 2, 6, 4, -1, -1, -1, -1, -1, -1, -1,
3, 0, 8, 1, 2, 9, 2, 4, 9, 2, 6, 4, -1, -1, -1, -1,
0, 2, 4, 4, 2, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
8, 3, 2, 8, 2, 4, 4, 2, 6, -1, -1, -1, -1, -1, -1, -1,
10, 4, 9, 10, 6, 4, 11, 2, 3, -1, -1, -1, -1, -1, -1, -1,
0, 8, 2, 2, 8, 11, 4, 9, 10, 4, 10, 6, -1, -1, -1, -1,
3, 11, 2, 0, 1, 6, 0, 6, 4, 6, 1, 10, -1, -1, -1, -1,
6, 4, 1, 6, 1, 10, 4, 8, 1, 2, 1, 11, 8, 11, 1, -1,
9, 6, 4, 9, 3, 6, 9, 1, 3, 11, 6, 3, -1, -1, -1, -1,
8, 11, 1, 8, 1, 0, 11, 6, 1, 9, 1, 4, 6, 4, 1, -1,
3, 11, 6, 3, 6, 0, 0, 6, 4, -1, -1, -1, -1, -1, -1, -1,
6, 4, 8, 11, 6, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
7, 10, 6, 7, 8, 10, 8, 9, 10, -1, -1, -1, -1, -1, -1, -1,
0, 7, 3, 0, 10, 7, 0, 9, 10, 6, 7, 10, -1, -1, -1, -1,
10, 6, 7, 1, 10, 7, 1, 7, 8, 1, 8, 0, -1, -1, -1, -1,
10, 6, 7, 10, 7, 1, 1, 7, 3, -1, -1, -1, -1, -1, -1, -1,
1, 2, 6, 1, 6, 8, 1, 8, 9, 8, 6, 7, -1, -1, -1, -1,
2, 6, 9, 2, 9, 1, 6, 7, 9, 0, 9, 3, 7, 3, 9, -1,
7, 8, 0, 7, 0, 6, 6, 0, 2, -1, -1, -1, -1, -1, -1, -1,
7, 3, 2, 6, 7, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
2, 3, 11, 10, 6, 8, 10, 8, 9, 8, 6, 7, -1, -1, -1, -1,
2, 0, 7, 2, 7, 11, 0, 9, 7, 6, 7, 10, 9, 10, 7, -1,
1, 8, 0, 1, 7, 8, 1, 10, 7, 6, 7, 10, 2, 3, 11, -1,
11, 2, 1, 11, 1, 7, 10, 6, 1, 6, 7, 1, -1, -1, -1, -1,
8, 9, 6, 8, 6, 7, 9, 1, 6, 11, 6, 3, 1, 3, 6, -1,
0, 9, 1, 11, 6, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
7, 8, 0, 7, 0, 6, 3, 11, 0, 11, 6, 0, -1, -1, -1, -1,
7, 11, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
7, 6, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
3, 0, 8, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 1, 9, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
8, 1, 9, 8, 3, 1, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1,
10, 1, 2, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 2, 10, 3, 0, 8, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1,
2, 9, 0, 2, 10, 9, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1,
6, 11, 7, 2, 10, 3, 10, 8, 3, 10, 9, 8, -1, -1, -1, -1,
7, 2, 3, 6, 2, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
7, 0, 8, 7, 6, 0, 6, 2, 0, -1, -1, -1, -1, -1, -1, -1,
2, 7, 6, 2, 3, 7, 0, 1, 9, -1, -1, -1, -1, -1, -1, -1,
1, 6, 2, 1, 8, 6, 1, 9, 8, 8, 7, 6, -1, -1, -1, -1,
10, 7, 6, 10, 1, 7, 1, 3, 7, -1, -1, -1, -1, -1, -1, -1,
10, 7, 6, 1, 7, 10, 1, 8, 7, 1, 0, 8, -1, -1, -1, -1,
0, 3, 7, 0, 7, 10, 0, 10, 9, 6, 10, 7, -1, -1, -1, -1,
7, 6, 10, 7, 10, 8, 8, 10, 9, -1, -1, -1, -1, -1, -1, -1,
6, 8, 4, 11, 8, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
3, 6, 11, 3, 0, 6, 0, 4, 6, -1, -1, -1, -1, -1, -1, -1,
8, 6, 11, 8, 4, 6, 9, 0, 1, -1, -1, -1, -1, -1, -1, -1,
9, 4, 6, 9, 6, 3, 9, 3, 1, 11, 3, 6, -1, -1, -1, -1,
6, 8, 4, 6, 11, 8, 2, 10, 1, -1, -1, -1, -1, -1, -1, -1,
1, 2, 10, 3, 0, 11, 0, 6, 11, 0, 4, 6, -1, -1, -1, -1,
4, 11, 8, 4, 6, 11, 0, 2, 9, 2, 10, 9, -1, -1, -1, -1,
10, 9, 3, 10, 3, 2, 9, 4, 3, 11, 3, 6, 4, 6, 3, -1,
8, 2, 3, 8, 4, 2, 4, 6, 2, -1, -1, -1, -1, -1, -1, -1,
0, 4, 2, 4, 6, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 9, 0, 2, 3, 4, 2, 4, 6, 4, 3, 8, -1, -1, -1, -1,
1, 9, 4, 1, 4, 2, 2, 4, 6, -1, -1, -1, -1, -1, -1, -1,
8, 1, 3, 8, 6, 1, 8, 4, 6, 6, 10, 1, -1, -1, -1, -1,
10, 1, 0, 10, 0, 6, 6, 0, 4, -1, -1, -1, -1, -1, -1, -1,
4, 6, 3, 4, 3, 8, 6, 10, 3, 0, 3, 9, 10, 9, 3, -1,
10, 9, 4, 6, 10, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 9, 5, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 8, 3, 4, 9, 5, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1,
5, 0, 1, 5, 4, 0, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1,
11, 7, 6, 8, 3, 4, 3, 5, 4, 3, 1, 5, -1, -1, -1, -1,
9, 5, 4, 10, 1, 2, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1,
6, 11, 7, 1, 2, 10, 0, 8, 3, 4, 9, 5, -1, -1, -1, -1,
7, 6, 11, 5, 4, 10, 4, 2, 10, 4, 0, 2, -1, -1, -1, -1,
3, 4, 8, 3, 5, 4, 3, 2, 5, 10, 5, 2, 11, 7, 6, -1,
7, 2, 3, 7, 6, 2, 5, 4, 9, -1, -1, -1, -1, -1, -1, -1,
9, 5, 4, 0, 8, 6, 0, 6, 2, 6, 8, 7, -1, -1, -1, -1,
3, 6, 2, 3, 7, 6, 1, 5, 0, 5, 4, 0, -1, -1, -1, -1,
6, 2, 8, 6, 8, 7, 2, 1, 8, 4, 8, 5, 1, 5, 8, -1,
9, 5, 4, 10, 1, 6, 1, 7, 6, 1, 3, 7, -1, -1, -1, -1,
1, 6, 10, 1, 7, 6, 1, 0, 7, 8, 7, 0, 9, 5, 4, -1,
4, 0, 10, 4, 10, 5, 0, 3, 10, 6, 10, 7, 3, 7, 10, -1,
7, 6, 10, 7, 10, 8, 5, 4, 10, 4, 8, 10, -1, -1, -1, -1,
6, 9, 5, 6, 11, 9, 11, 8, 9, -1, -1, -1, -1, -1, -1, -1,
3, 6, 11, 0, 6, 3, 0, 5, 6, 0, 9, 5, -1, -1, -1, -1,
0, 11, 8, 0, 5, 11, 0, 1, 5, 5, 6, 11, -1, -1, -1, -1,
6, 11, 3, 6, 3, 5, 5, 3, 1, -1, -1, -1, -1, -1, -1, -1,
1, 2, 10, 9, 5, 11, 9, 11, 8, 11, 5, 6, -1, -1, -1, -1,
0, 11, 3, 0, 6, 11, 0, 9, 6, 5, 6, 9, 1, 2, 10, -1,
11, 8, 5, 11, 5, 6, 8, 0, 5, 10, 5, 2, 0, 2, 5, -1,
6, 11, 3, 6, 3, 5, 2, 10, 3, 10, 5, 3, -1, -1, -1, -1,
5, 8, 9, 5, 2, 8, 5, 6, 2, 3, 8, 2, -1, -1, -1, -1,
9, 5, 6, 9, 6, 0, 0, 6, 2, -1, -1, -1, -1, -1, -1, -1,
1, 5, 8, 1, 8, 0, 5, 6, 8, 3, 8, 2, 6, 2, 8, -1,
1, 5, 6, 2, 1, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 3, 6, 1, 6, 10, 3, 8, 6, 5, 6, 9, 8, 9, 6, -1,
10, 1, 0, 10, 0, 6, 9, 5, 0, 5, 6, 0, -1, -1, -1, -1,
0, 3, 8, 5, 6, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
10, 5, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
11, 5, 10, 7, 5, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
11, 5, 10, 11, 7, 5, 8, 3, 0, -1, -1, -1, -1, -1, -1, -1,
5, 11, 7, 5, 10, 11, 1, 9, 0, -1, -1, -1, -1, -1, -1, -1,
10, 7, 5, 10, 11, 7, 9, 8, 1, 8, 3, 1, -1, -1, -1, -1,
11, 1, 2, 11, 7, 1, 7, 5, 1, -1, -1, -1, -1, -1, -1, -1,
0, 8, 3, 1, 2, 7, 1, 7, 5, 7, 2, 11, -1, -1, -1, -1,
9, 7, 5, 9, 2, 7, 9, 0, 2, 2, 11, 7, -1, -1, -1, -1,
7, 5, 2, 7, 2, 11, 5, 9, 2, 3, 2, 8, 9, 8, 2, -1,
2, 5, 10, 2, 3, 5, 3, 7, 5, -1, -1, -1, -1, -1, -1, -1,
8, 2, 0, 8, 5, 2, 8, 7, 5, 10, 2, 5, -1, -1, -1, -1,
9, 0, 1, 5, 10, 3, 5, 3, 7, 3, 10, 2, -1, -1, -1, -1,
9, 8, 2, 9, 2, 1, 8, 7, 2, 10, 2, 5, 7, 5, 2, -1,
1, 3, 5, 3, 7, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 8, 7, 0, 7, 1, 1, 7, 5, -1, -1, -1, -1, -1, -1, -1,
9, 0, 3, 9, 3, 5, 5, 3, 7, -1, -1, -1, -1, -1, -1, -1,
9, 8, 7, 5, 9, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
5, 8, 4, 5, 10, 8, 10, 11, 8, -1, -1, -1, -1, -1, -1, -1,
5, 0, 4, 5, 11, 0, 5, 10, 11, 11, 3, 0, -1, -1, -1, -1,
0, 1, 9, 8, 4, 10, 8, 10, 11, 10, 4, 5, -1, -1, -1, -1,
10, 11, 4, 10, 4, 5, 11, 3, 4, 9, 4, 1, 3, 1, 4, -1,
2, 5, 1, 2, 8, 5, 2, 11, 8, 4, 5, 8, -1, -1, -1, -1,
0, 4, 11, 0, 11, 3, 4, 5, 11, 2, 11, 1, 5, 1, 11, -1,
0, 2, 5, 0, 5, 9, 2, 11, 5, 4, 5, 8, 11, 8, 5, -1,
9, 4, 5, 2, 11, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
2, 5, 10, 3, 5, 2, 3, 4, 5, 3, 8, 4, -1, -1, -1, -1,
5, 10, 2, 5, 2, 4, 4, 2, 0, -1, -1, -1, -1, -1, -1, -1,
3, 10, 2, 3, 5, 10, 3, 8, 5, 4, 5, 8, 0, 1, 9, -1,
5, 10, 2, 5, 2, 4, 1, 9, 2, 9, 4, 2, -1, -1, -1, -1,
8, 4, 5, 8, 5, 3, 3, 5, 1, -1, -1, -1, -1, -1, -1, -1,
0, 4, 5, 1, 0, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
8, 4, 5, 8, 5, 3, 9, 0, 5, 0, 3, 5, -1, -1, -1, -1,
9, 4, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 11, 7, 4, 9, 11, 9, 10, 11, -1, -1, -1, -1, -1, -1, -1,
0, 8, 3, 4, 9, 7, 9, 11, 7, 9, 10, 11, -1, -1, -1, -1,
1, 10, 11, 1, 11, 4, 1, 4, 0, 7, 4, 11, -1, -1, -1, -1,
3, 1, 4, 3, 4, 8, 1, 10, 4, 7, 4, 11, 10, 11, 4, -1,
4, 11, 7, 9, 11, 4, 9, 2, 11, 9, 1, 2, -1, -1, -1, -1,
9, 7, 4, 9, 11, 7, 9, 1, 11, 2, 11, 1, 0, 8, 3, -1,
11, 7, 4, 11, 4, 2, 2, 4, 0, -1, -1, -1, -1, -1, -1, -1,
11, 7, 4, 11, 4, 2, 8, 3, 4, 3, 2, 4, -1, -1, -1, -1,
2, 9, 10, 2, 7, 9, 2, 3, 7, 7, 4, 9, -1, -1, -1, -1,
9, 10, 7, 9, 7, 4, 10, 2, 7, 8, 7, 0, 2, 0, 7, -1,
3, 7, 10, 3, 10, 2, 7, 4, 10, 1, 10, 0, 4, 0, 10, -1,
1, 10, 2, 8, 7, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 9, 1, 4, 1, 7, 7, 1, 3, -1, -1, -1, -1, -1, -1, -1,
4, 9, 1, 4, 1, 7, 0, 8, 1, 8, 7, 1, -1, -1, -1, -1,
4, 0, 3, 7, 4, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
4, 8, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
9, 10, 8, 10, 11, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
3, 0, 9, 3, 9, 11, 11, 9, 10, -1, -1, -1, -1, -1, -1, -1,
0, 1, 10, 0, 10, 8, 8, 10, 11, -1, -1, -1, -1, -1, -1, -1,
3, 1, 10, 11, 3, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 2, 11, 1, 11, 9, 9, 11, 8, -1, -1, -1, -1, -1, -1, -1,
3, 0, 9, 3, 9, 11, 1, 2, 9, 2, 11, 9, -1, -1, -1, -1,
0, 2, 11, 8, 0, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
3, 2, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
2, 3, 8, 2, 8, 10, 10, 8, 9, -1, -1, -1, -1, -1, -1, -1,
9, 10, 2, 0, 9, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
2, 3, 8, 2, 8, 10, 0, 1, 8, 1, 10, 8, -1, -1, -1, -1,
1, 10, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
1, 3, 8, 9, 1, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 9, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
0, 3, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1};


    // Update is called once per frame
    void Update()
    {

        //if (target is null)
        //{
        //    target = new(16, 16, 1, RenderTextureFormat.ARGB32, 1);

        //    target.enableRandomWrite = true;

        //    target.Create();
        //    outTarget = target;


        //    var triTable = new Texture2D(TriTable.Length, 1, TextureFormat.R16, false);
        //    triTable.LoadRawTextureData<short>(new NativeArray<short>(TriTable,Allocator.Temp));
        //    using ComputeBuffer counterBuffer = new ComputeBuffer(65536, sizeof(int) * 3, ComputeBufferType.Append);
        //    counterBuffer.SetCounterValue(0);

        //    var kernel = shader.FindKernel("Test");
        //    shader.SetBuffer(kernel, "TestBuffer", counterBuffer);
        //    shader.SetTexture(kernel, "Result", target);
        //    shader.SetTexture(kernel, "TriTable", triTable);
            
        //    shader.Dispatch(kernel, target.width/8, target.height/8, 1);

        //    using ComputeBuffer counterValueBuffer = new(1, 4, ComputeBufferType.Raw);

        //    ComputeBuffer.CopyCount(counterBuffer, counterValueBuffer, 0);
        //    var asyncRequest = AsyncGPUReadback.Request(counterValueBuffer, counterValueBuffer.stride, 0);
        //    asyncRequest.WaitForCompletion();

        //    //int[] countAr = new int[1];
        //    var count = asyncRequest.GetData<int>()[0];
        //    //var count = countAr[0];

        //    Debug.Log("count: " + count);


        //    int[] data = new int[count * 3];
        //    counterBuffer.GetData(data);


        //    countResult = new Vector3Int[count];
        //    for (int i = 0; i < count; i++)
        //        countResult[i] = new(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);


        //}



    }
}
