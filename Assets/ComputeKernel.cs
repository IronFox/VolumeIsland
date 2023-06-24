#define LOW_RESOLUTION

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;
using static IslandMain;

public class ComputeKernel : IDisposable
{
    /// <summary>
    /// 4 bytes large: contains number of cells to be dispatched indirectly
    /// </summary>
    public ComputeBuffer SharedJobSizeBuffer { get; private set; }
    /// <summary>
    /// Huge: vertex out append buffer
    /// </summary>
    public ComputeBuffer SharedVertexBuffer { get; private set; }
    /// <summary>
    /// Huge: index out append buffer
    /// </summary>
    public ComputeBuffer SharedIndexBuffer { get; private set; }
    /// <summary>
    /// Huge: cells determined by vertex emitter to have some trianlges in them
    /// XYZ = cell coordinates, W = triangle batch index derived from corners
    /// </summary>
    public ComputeBuffer SharedCellBuffer { get; private set; }
    /// <summary>
    /// Huge: map of all edge vertexes generated along X axis
    /// </summary>
    public RenderTexture SharedVertexIndexMapX { get; private set; }
    /// <summary>
    /// Huge: map of all edge vertexes generated along Y axis
    /// </summary>
    public RenderTexture SharedVertexIndexMapY { get; private set; }
    /// <summary>
    /// Huge: map of all edge vertexes generated along Z axis
    /// </summary>
    public RenderTexture SharedVertexIndexMapZ { get; private set; }

#if !LOW_RESOLUTION
    public RenderTexture SharedUpscaledDensityMap { get; private set;  }
#endif


    public void Dispose()
    {
        SharedJobSizeBuffer.Dispose();
        SharedVertexBuffer.Dispose();
        SharedIndexBuffer.Dispose();
        UnityEngine.Object.Destroy(SharedVertexIndexMapX);
        UnityEngine.Object.Destroy(SharedVertexIndexMapY);
        UnityEngine.Object.Destroy(SharedVertexIndexMapZ);
        Resolver.Dispose();
        #if !LOW_RESOLUTION
            UnityEngine.Object.Destroy(SharedUpscaledDensityMap);
        #endif
    }

    public ComputeKernel(
        ComputeShader? generateTerrain,
        ComputeShader? emitVertexes,
        ComputeShader? marchingCubes,
        ComputeShader? compact


        )
    {
        if (generateTerrain is null)
            throw new ArgumentNullException(nameof(generateTerrain));
        if (emitVertexes is null)
            throw new ArgumentNullException(nameof(emitVertexes));
        if (marchingCubes is null)
            throw new ArgumentNullException(nameof(marchingCubes));
        if (compact is null)
            throw new ArgumentNullException(nameof(compact));
        GenerateTerrainShader = generateTerrain;
        EmitVertexes = emitVertexes;
        MarchingCubes = marchingCubes;
        Compact = compact;

        GenerateTerrainKernel = GenerateTerrainShader.FindKernel("Main");
        EmitVertexesKernel = EmitVertexes.FindKernel("EmitVertex");
        EmitTrianglesKernel = MarchingCubes.FindKernel("EmitTriangles");

        CopyVertexKernel = Compact.FindKernel("CopyVertex");
        CopyIndexKernel = Compact.FindKernel("CopyIndex");

#if !LOW_RESOLUTION
        SharedUpscaledDensityMap = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.R16);
#endif
        SharedJobSizeBuffer = new(3, 4, ComputeBufferType.IndirectArguments);
        SharedJobSizeBuffer.SetData(new int[] { 1, 1, 1 });

        SharedVertexIndexMapX = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);
        SharedVertexIndexMapY = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);
        SharedVertexIndexMapZ = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);


        SharedIndexBuffer = new(Sector.TotalOutputVoxelCount * 5, 12, ComputeBufferType.Append);
        SharedVertexBuffer = new(Sector.TotalOutputVoxelCount * 3, VertexLayout.SizeBytes, ComputeBufferType.Counter);
        Debug.Log($"Vertex buffer accomodates {SharedVertexBuffer.count} vertexes in {SharedVertexBuffer.count * SharedVertexBuffer.stride} bytes");
        SharedVertexBuffer.SetCounterValue(0);

        if (Sector.OutputSizeInVoxels > 256)
            throw new InvalidOperationException("Bad voxel volume for 8bit indexes");
        SharedCellBuffer = new(Sector.TotalOutputVoxelCount, 4, ComputeBufferType.Append); //x,y,z,i = 4*(byte)
    }


    public ComputeShader GenerateTerrainShader { get; }
    public ComputeShader EmitVertexes { get; }
    public ComputeShader MarchingCubes { get; }
    public ComputeShader Compact { get; }

    public ComputeOperator Resolver { get; } = new();


    public int GenerateTerrainKernel { get; }
    public int EmitVertexesKernel { get; }
    public int EmitTrianglesKernel { get; }
    public int CopyVertexKernel { get; }
    public int CopyIndexKernel { get; }


    private static void Dispatch(ComputeShader shader, int kernel, int resolution)
    {
        int x = (resolution + Sector.KernelSize - 1) / Sector.KernelSize;
        shader.Dispatch(kernel, x, x, x);

    }

    public interface IDebugOut
    {
        void Log(string msg);
        void DrawPoint(float x, float y, float z, Color c);
        void DrawLine(Vector3 p0, Vector3 p1, Color c);

    }

    private static string Ar<T>(IEnumerable<T> array) => "[" + string.Join(",", array) + "]";
    private static string Ar(IEnumerable<float> floatArray) => "[" + string.Join(",", floatArray.Select(vi => vi.ToString(CultureInfo.InvariantCulture))) + "]";
    private static string Ar(IEnumerable<double> doubleArray) => "[" + string.Join(",", doubleArray.Select(vi => vi.ToString(CultureInfo.InvariantCulture))) + "]";

    private static Vector3 V(float x) => new(x, x, x);
    private static Vector3 V(float x, float y, float z) => new(x, y, z);
    private static Vector3 V(float[] v, int offset) => new(v[offset], v[offset + 1], v[offset + 2]);


    public void GenerateTerrain(Sector sector)
    {
        GenerateTerrainShader.SetTexture(GenerateTerrainKernel, "DensityVolume", sector.DensityMap);
        GenerateTerrainShader.SetTexture(GenerateTerrainKernel, "MaterialVolume", sector.MaterialMap);
        GenerateTerrainShader.SetVector("Origin", sector.Origin);
        GenerateTerrainShader.SetFloat("VoxelSize", Sector.InputVoxelSize);
        GenerateTerrainShader.SetInt("SizeInVoxels", Sector.InputSizeInVoxels);

        Dispatch(GenerateTerrainShader, GenerateTerrainKernel, Sector.InputSizeInVoxels);

    }

    public async Task CompileAsync(Sector sector, IDebugOut? debugOut = null)
    {
        try
        {



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




            SharedVertexBuffer.SetCounterValue(0);
            SharedCellBuffer.SetCounterValue(0);
#if !LOW_RESOLUTION
        EmitVertexes.SetTexture(EmitVertexesKernel, "Volume", SharedUpscaledDensityMap);
#else
            EmitVertexes.SetTexture(EmitVertexesKernel, "Volume", sector.DensityMap);
#endif
            EmitVertexes.SetVector("Origin", sector.Origin);
            EmitVertexes.SetFloat("VoxelSize", Sector.OutputVoxelSize);
            EmitVertexes.SetInt("SizeInVoxels", Sector.OutputSizeInVoxels);
            EmitVertexes.SetBuffer(EmitVertexesKernel, "VertexOut", SharedVertexBuffer);
            EmitVertexes.SetBuffer(EmitVertexesKernel, "CellOut", SharedCellBuffer);
            EmitVertexes.SetTexture(EmitVertexesKernel, "IndexOutMapX", SharedVertexIndexMapX);
            EmitVertexes.SetTexture(EmitVertexesKernel, "IndexOutMapY", SharedVertexIndexMapY);
            EmitVertexes.SetTexture(EmitVertexesKernel, "IndexOutMapZ", SharedVertexIndexMapZ);
            //EmitVertexes.SetBuffer(kernel, "DebugIndexOutX", DebugIndexOutX);
            //EmitVertexes.SetBuffer(kernel, "DebugIndexOutY", DebugIndexOutY);
            //EmitVertexes.SetBuffer(kernel, "DebugIndexOutZ", DebugIndexOutZ);

            Dispatch(EmitVertexes, EmitVertexesKernel, Sector.OutputSizeInVoxels);


            var vertexCount = Resolver.GetAsync(SharedVertexBuffer);


            float[]? outVertexData = null;

            if (debugOut is not null)
            {
                var vcnt = await vertexCount;
                debugOut.Log("Emitted vertexes: " + vcnt);

                debugOut.Log("Emitted cells: " + (await Resolver.GetAsync(SharedCellBuffer)));
                var cells = (await ComputeOperator.GetAllAsync<byte>(SharedCellBuffer, 1, 4, 0)).ToArray();
                debugOut.Log($"First Cell: {string.Join(", ", cells)}");
                debugOut.Log($"Bits: {((int)cells[3]).ToBinaryString()}");

                var requestBytes = VertexLayout.SizeBytes * vcnt;
                debugOut.Log("Requesting " + requestBytes + " bytes");

                outVertexData = (await ComputeOperator.GetAllAsync<float>(SharedVertexBuffer, VertexLayout.SizeBytes, vcnt, 0)).ToArray();
                debugOut.Log($"Got {outVertexData.Length} floats from VRAM");


                //int xCnt = Resolver.GetNow(DebugIndexOutX);
                //int yCnt = Resolver.GetNow(DebugIndexOutY);
                //int zCnt = Resolver.GetNow(DebugIndexOutZ);

                //Debug.Log($"Got {xCnt},{yCnt},{zCnt} indexes");

                //int[] debugIndexOutX = ComputeOperator.GetAllNow<int>(DebugIndexOutX, 4, xCnt * 4, 0).ToArray();
                //int[] debugIndexOutY = ComputeOperator.GetAllNow<int>(DebugIndexOutY, 4, yCnt * 4, 0).ToArray();
                //int[] debugIndexOutZ = ComputeOperator.GetAllNow<int>(DebugIndexOutZ, 4, zCnt * 4, 0).ToArray();

                //Debug.Log($"X:{Ar(debugIndexOutX)}");
                //Debug.Log($"Y:{Ar(debugIndexOutY)}");
                //Debug.Log($"Z:{Ar(debugIndexOutZ)}");




                for (int i = 0; i < Sector.OutputSizeInVoxels; i++)
                    for (int j = 0; j < Sector.OutputSizeInVoxels; j++)
                        for (int k = 0; k < Sector.OutputSizeInVoxels; k++)
                        {
                            debugOut.DrawLine(
                                sector.Origin + new Vector3(i, j, 0) * Sector.OutputVoxelSize,
                                sector.Origin + new Vector3(i, j, Sector.CoreSizeInInputVoxels) * Sector.OutputVoxelSize,
                                Color.gray);
                            debugOut.DrawLine(
                                sector.Origin + new Vector3(i, 0, j) * Sector.OutputVoxelSize,
                                sector.Origin + new Vector3(i, Sector.CoreSizeInInputVoxels, j) * Sector.OutputVoxelSize,
                                Color.gray);
                            debugOut.DrawLine(
                                sector.Origin + new Vector3(0, i, j) * Sector.OutputVoxelSize,
                                sector.Origin + new Vector3(Sector.CoreSizeInInputVoxels, i, j) * Sector.OutputVoxelSize,
                                Color.gray);
                        }
                for (int i = 0; i < vcnt; i++)
                {
                    float x = outVertexData[i * VertexLayout.SizeFloats];
                    float y = outVertexData[i * VertexLayout.SizeFloats + 1];
                    float z = outVertexData[i * VertexLayout.SizeFloats + 2];
                    debugOut.DrawPoint(x, y, z, UnityEngine.Color.red);
                }
            }



            ComputeBuffer.CopyCount(SharedCellBuffer, SharedJobSizeBuffer, 0);

            if (debugOut is not null)
            {
                var cnt = (await ComputeOperator.GetAllAsync<int>(SharedJobSizeBuffer, 4, 3, 0));
                Debug.Log($"Size: {cnt[0]},{cnt[1]},{cnt[2]}");
            }

            SharedIndexBuffer.SetCounterValue(0);
            MarchingCubes.SetBuffer(EmitTrianglesKernel, "CellTable", SharedCellBuffer);
            MarchingCubes.SetBuffer(EmitTrianglesKernel, "IndexOutBuffer", SharedIndexBuffer);
            MarchingCubes.SetTexture(EmitTrianglesKernel, "IndexInMapX", SharedVertexIndexMapX);
            MarchingCubes.SetTexture(EmitTrianglesKernel, "IndexInMapY", SharedVertexIndexMapY);
            MarchingCubes.SetTexture(EmitTrianglesKernel, "IndexInMapZ", SharedVertexIndexMapZ);
            MarchingCubes.DispatchIndirect(EmitTrianglesKernel, SharedJobSizeBuffer);


            var numTriangles = Resolver.GetAsync(SharedIndexBuffer);
            if (debugOut is not null)
            {

                var numT = await numTriangles;
                Debug.Log($"Emitted index triples: {numT}");

                var indexes = ComputeOperator.GetAllNow<int>(SharedIndexBuffer, 4, numT * 3, 0);
                Debug.Log($"Indexes: {Ar(indexes)}");


                for (int i = 0; i < numT; i++)
                {
                    var o0 = indexes[i * 3];
                    var o1 = indexes[i * 3 + 1];
                    var o2 = indexes[i * 3 + 2];

                    var v0 = V(outVertexData!, o0 * VertexLayout.SizeFloats);
                    var v1 = V(outVertexData!, o1 * VertexLayout.SizeFloats);
                    var v2 = V(outVertexData!, o2 * VertexLayout.SizeFloats);

                    var c = new Color(0, 0, 1, 0.5f);
                    debugOut.DrawLine(v0, v1, c);
                    debugOut.DrawLine(v1, v2, c);
                    debugOut.DrawLine(v2, v0, c);
                }

            }
            {
                var numT = await numTriangles;
                var numV = await vertexCount;

                GraphicsBuffer v = new (GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw, numV, VertexLayout.SizeBytes);
                GraphicsBuffer i = new (GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw, numT * 3, 4);
                
                Compact.SetBuffer(CopyIndexKernel, "IndexSource", SharedIndexBuffer);
                Compact.SetBuffer(CopyIndexKernel, "IndexDestination", i);
                Compact.Dispatch(CopyIndexKernel, numT,1,1);

                Compact.SetBuffer(CopyVertexKernel, "VertexSource", SharedVertexBuffer);
                Compact.SetBuffer(CopyVertexKernel, "VertexDestination", v);
                Compact.Dispatch(CopyVertexKernel, numV, 1, 1);


                //test:
                //i.SetData(new int[] { 0, 1, 2 });
                //v.SetData(new float[] {
                //    0,0,0, 0,0,1,
                //    10,0,0, 0,0,1,
                //    10, 10, 0, 0,0,1,
                //    0, 10, 0, 0, 0, 1
                //});
                //numT = 1;
                //numV = 4;


                if (debugOut is not null)
                {
                    int[] test = new int[3];
                    i.GetData(test, 0, 0, 3);
                    debugOut?.Log("Indexes in final buffer: " + Ar(test));
                    float[] vtest = new float[6*3];
                    //v.SetData(new float[] { 0, 1, 2, 3, 4, 5 });
                    v.GetData(vtest, 0, 0, 6*3);
                    debugOut?.Log("Vertex floats in final buffer: " + Ar(vtest));
                }



                Sector.Compacted baked = new(v, i, numV, numT);

                await sector.ReplaceCompactAsync(baked);
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
}
