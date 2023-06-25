//#define LOW_RESOLUTION

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;
using static ComputeKernel;
using static IslandMain;
using static Sector;
using static UnityEngine.GraphicsBuffer;
using Debug = UnityEngine.Debug;


public record BakeJob(OvenSlot OvenSlot, Task<int> VertexCount, Task<int> TriangleCount, Sector Sector)
{

    public async Task BakeAsync(Stopwatch watch, IDebugOut? debugOut)
    {
        try
        {

            float[]? outVertexData = null;
            int[]? outIndexData = null;
            if (debugOut is not null)
            {

                var vcnt = await VertexCount;
                debugOut.Log("Emitted vertexes: " + vcnt + "/" + OvenSlot.SharedVertexBuffer.count);

                //debugOut.Log("Emitted cells: " + (await ComputeOperator.GetCountAsync(SharedCellBuffer)) + "/" + SharedCellBuffer.count);
                //var cells = (await ComputeOperator.GetAllAsync<byte>(SharedCellBuffer, 1, 4, 0)).ToArray();
                //debugOut.Log($"First Cell: {string.Join(", ", cells)}");
                //debugOut.Log($"Bits: {((int)cells[3]).ToBinaryString()}");

                if (vcnt < 1000)
                {
                    var requestBytes = VertexLayout.SizeBytes * vcnt;
                    debugOut.Log("Requesting " + requestBytes + " bytes");

                    outVertexData = (await ComputeOperator.GetAllAsync<float>(OvenSlot.SharedVertexBuffer, VertexLayout.SizeBytes, vcnt, 0)).ToArray();
                    debugOut.Log($"Got {outVertexData.Length} floats from VRAM");
                }

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


#pragma warning disable CS0162
                if (Sector.OutputSizeInVoxels < 10)
                {
                    for (int i = 0; i < Sector.OutputSizeInVoxels; i++)
                        for (int j = 0; j < Sector.OutputSizeInVoxels; j++)
                            for (int k = 0; k < Sector.OutputSizeInVoxels; k++)
                            {
                                debugOut.DrawLine(
                                    Sector.Origin + new Vector3(i, j, 0) * Sector.OutputVoxelSize,
                                    Sector.Origin + new Vector3(i, j, Sector.CoreSizeInInputVoxels) * Sector.OutputVoxelSize,
                                    Color.gray);
                                debugOut.DrawLine(
                                    Sector.Origin + new Vector3(i, 0, j) * Sector.OutputVoxelSize,
                                    Sector.Origin + new Vector3(i, Sector.CoreSizeInInputVoxels, j) * Sector.OutputVoxelSize,
                                    Color.gray);
                                debugOut.DrawLine(
                                    Sector.Origin + new Vector3(0, i, j) * Sector.OutputVoxelSize,
                                    Sector.Origin + new Vector3(Sector.CoreSizeInInputVoxels, i, j) * Sector.OutputVoxelSize,
                                    Color.gray);
                            }
                }
#pragma warning restore
                if (outVertexData is not null)
                    for (int i = 0; i < vcnt; i++)
                    {
                        float x = outVertexData[i * VertexLayout.SizeFloats];
                        float y = outVertexData[i * VertexLayout.SizeFloats + 1];
                        float z = outVertexData[i * VertexLayout.SizeFloats + 2];
                        debugOut.DrawPoint(x, y, z, UnityEngine.Color.red);
                    }
            }


            if (debugOut is not null)
            {

                var lNumT = await TriangleCount;
                debugOut.Log($"Emitted index triples: {lNumT}");


                if (outVertexData is not null)
                {
                    outIndexData = ComputeOperator.GetAllNow<int>(OvenSlot.SharedIndexBuffer, 4, lNumT * 3, 0);
                    debugOut.Log($"Indexes: {Ar(outIndexData)}");
                    int failCount = 0;
                    for (int i = 0; i < lNumT; i++)
                    {
                        try
                        {
                            var o0 = outIndexData[i * 3];
                            var o1 = outIndexData[i * 3 + 1];
                            var o2 = outIndexData[i * 3 + 2];

                            try
                            {
                                var v0 = ComputeKernel.V(outVertexData, o0 * VertexLayout.SizeFloats);
                                var v1 = ComputeKernel.V(outVertexData, o1 * VertexLayout.SizeFloats);
                                var v2 = ComputeKernel.V(outVertexData, o2 * VertexLayout.SizeFloats);

                                var c = new Color(0, 0, 1, 0.5f);
                                debugOut.DrawLine(v0, v1, c);
                                debugOut.DrawLine(v1, v2, c);
                                debugOut.DrawLine(v2, v0, c);
                            }
                            catch (Exception ex)
                            {
                                failCount++;
                                Debug.LogError($"Failed to rasterized triangle #{i}/{lNumT} @{o0},{o1},{o2} / {outVertexData.Length}");
                                Debug.LogException(ex);
                            }
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            Debug.LogError($"Failed to rasterized triangle #{i}/{lNumT}");
                            Debug.LogException(ex);
                        }



                    }
                    if (failCount > 0)
                        Debug.LogError($"Failed to rasterize {failCount} triangles");
                }

            }

            int numT = 0;
            int numV = 0;

            await Profile("Fetch 1", async () =>
            {
                numV = await VertexCount;
            });
            await Profile("Fetch 2", async () =>
            {
                numT = await TriangleCount;
            });



            await Profile("Bake", async () =>
            {
                if (numT > 0)
                {
                    GraphicsBuffer vertexes = new(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw, numV, VertexLayout.SizeBytes);
                    GraphicsBuffer indexes = new(GraphicsBuffer.Target.Index | GraphicsBuffer.Target.Raw, numT * 3, 4);

                    OvenSlot.Compact.SetBuffer(OvenSlot.CopyIndexKernel, "IndexSource", OvenSlot.SharedIndexBuffer);
                    OvenSlot.Compact.SetBuffer(OvenSlot.CopyIndexKernel, "IndexDestination", indexes);
                    ComputeOperator.DispatchOversized(OvenSlot.Compact, OvenSlot.CopyIndexKernel, numT);

                    OvenSlot.Compact.SetBuffer(OvenSlot.CopyVertexKernel, "VertexSource", OvenSlot.SharedVertexBuffer);
                    OvenSlot.Compact.SetBuffer(OvenSlot.CopyVertexKernel, "VertexDestination", vertexes);
                    ComputeOperator.DispatchOversized(OvenSlot.Compact, OvenSlot.CopyVertexKernel, numV);


                    if (debugOut is not null && outVertexData is not null)
                    {
                        var vCheck = await ComputeOperator.GetAllAsync<float>(vertexes, VertexLayout.SizeBytes, numV, 0);
                        //bool vError = false;
                        for (int i = 0; i < numV * VertexLayout.SizeFloats; i++)
                            if (vCheck[i] != outVertexData[i])
                            {
                                Debug.LogError($"Vertex data difference at float {i}/{numV * VertexLayout.SizeFloats}: {vCheck[i]} != {outVertexData[i]}");
                                //vError = true;
                                break;
                            }
                        //bool iError = false;
                        //var iCheck = await ComputeOperator.GetAllAsync<int>(indexes, 4, numT * 3, 0);
                        //for (int i = 0; i < numT * 3; i++)
                        //    if (iCheck[i] != outIndexData![i])
                        //    {
                        //        Debug.LogError($"Index data difference at int {i}/{numV * VertexLayout.SizeFloats}: {iCheck[i]} != {outIndexData[i]}");
                        //        iError = true;
                        //        break;
                        //    }
                        //Debug.Log($"Checked {numV} vertexes and {numT} triangles. vError={vError} iError={iError}");

                    }


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
                        //var numErr = await numErrors;
                        //if (numErr > 0)
                        //{
                        //    Debug.LogError($"Encountered {numErr} cell errors");
                        //    var errorBytes = await ComputeOperator.GetAllAsync<byte>(SharedErrorBuffer, 16, numErr);
                        //    //Debug.LogError($"Got {errorBytes.Length} bytes");
                        //    for (int i = 0; i < numErr; i++)
                        //    {
                        //        byte cellX = errorBytes[i * 16];
                        //        byte cellY = errorBytes[i * 16+1];
                        //        byte cellZ = errorBytes[i * 16+2];
                        //        if (cellX != 7 || cellY != 6 || cellZ != 15)
                        //            continue;
                        //        byte type = errorBytes[i * 16 + 3];
                        //        int i0 = BitConverter.ToInt32(errorBytes, i * 16 + 4);
                        //        int i1 = BitConverter.ToInt32(errorBytes, i * 16 + 8);
                        //        int i2 = BitConverter.ToInt32(errorBytes, i * 16 + 12);

                        //        Debug.LogError($"Failed to find edge vertexes in cell {cellX},{cellY},{cellZ}, ebits = {((int)type).ToBinaryString()}: {i0},{i1},{i2}");


                        //        Vector3 from = sector.Origin + Sector.OutputVoxelSize * new Vector3(cellX, cellY, cellZ);

                        //        debugOut.DrawBox(from, from + V(Sector.OutputVoxelSize), Color.yellow);

                        //    }
                        //}


                        int[] test = new int[3];
                        indexes.GetData(test, 0, 0, 3);
                        debugOut?.Log("Indexes in final buffer: " + Ar(test));
                        float[] vtest = new float[6 * 3];
                        //v.SetData(new float[] { 0, 1, 2, 3, 4, 5 });
                        vertexes.GetData(vtest, 0, 0, 6 * 3);
                        debugOut?.Log("Vertex floats in final buffer: " + Ar(vtest));
                    }



                    Sector.Compacted baked = new(vertexes, indexes, numV, numT);

                    await Sector.ReplaceCompactAsync(baked);

                    Debug.Log($"Sector {Sector.Origin} compiled in {watch.Elapsed}");
                }
                else
                {
                    Debug.Log($"Sector {Sector.Origin} compiled (no triangles) in {watch.Elapsed}");
                    await Sector.ReplaceCompactAsync(null);
                }
            });
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
}

public class OvenSlot : IDisposable
{
    /// <summary>
    /// Huge: vertex out append buffer
    /// </summary>
    public ComputeBuffer SharedVertexBuffer { get; }
    /// <summary>
    /// Huge: index out append buffer
    /// </summary>
    public ComputeBuffer SharedIndexBuffer { get; }

    public ComputeShader Compact { get; }

    public int CopyVertexKernel { get; }
    public int CopyIndexKernel { get; }

    public void Dispose()
    {
        SharedVertexBuffer.Dispose();
        SharedIndexBuffer.Dispose();
    }

    public OvenSlot(ComputeShader? compact)
    {
        if (compact is null)
            throw new ArgumentNullException(nameof(compact));
        Compact = compact;
        CopyVertexKernel = Compact.FindKernel("CopyVertex");
        CopyIndexKernel = Compact.FindKernel("CopyIndex");

        SharedIndexBuffer = new(Sector.TotalOutputVoxelCount * 5, 12, ComputeBufferType.Append);
        SharedVertexBuffer = new(Sector.TotalOutputVoxelCount * 3, VertexLayout.SizeBytes, ComputeBufferType.Counter);
        Debug.Log($"Vertex buffer accomodates {SharedVertexBuffer.count} vertexes in {SharedVertexBuffer.count * SharedVertexBuffer.stride} bytes");
    }





}

public class ComputeKernel : IDisposable
{
    /// <summary>
    /// 4 bytes large: contains number of cells to be dispatched indirectly
    /// </summary>
    public ComputeBuffer SharedJobSizeBuffer { get; }
    /// <summary>
    /// Huge: cells determined by vertex emitter to have some trianlges in them
    /// XYZ = cell coordinates, W = triangle batch index derived from corners
    /// </summary>
    public ComputeBuffer SharedCellBuffer { get; }
    /// <summary>
    /// Huge: map of all edge vertexes generated along X axis
    /// </summary>
    public RenderTexture SharedVertexIndexMapX { get; }
    /// <summary>
    /// Huge: map of all edge vertexes generated along Y axis
    /// </summary>
    public RenderTexture SharedVertexIndexMapY { get; }
    /// <summary>
    /// Huge: map of all edge vertexes generated along Z axis
    /// </summary>
    public RenderTexture SharedVertexIndexMapZ { get; }

#if !LOW_RESOLUTION
    public RenderTexture SharedUpscaledDensityMap { get;  }
#endif


    public void Dispose()
    {
        SharedJobSizeBuffer.Dispose();
        UnityEngine.Object.Destroy(SharedVertexIndexMapX);
        UnityEngine.Object.Destroy(SharedVertexIndexMapY);
        UnityEngine.Object.Destroy(SharedVertexIndexMapZ);
        #if !LOW_RESOLUTION
            UnityEngine.Object.Destroy(SharedUpscaledDensityMap);
        #endif
    }

    public ComputeKernel(
        ComputeShader? generateTerrain,
        ComputeShader? emitVertexes,
        ComputeShader? marchingCubes,
        ComputeShader? upscaleTerrain
        )
    {
        if (generateTerrain is null)
            throw new ArgumentNullException(nameof(generateTerrain));
        if (emitVertexes is null)
            throw new ArgumentNullException(nameof(emitVertexes));
        if (marchingCubes is null)
            throw new ArgumentNullException(nameof(marchingCubes));
        if (upscaleTerrain is null)
            throw new ArgumentNullException(nameof(upscaleTerrain));
        GenerateTerrainShader = generateTerrain;
        EmitVertexes = emitVertexes;
        MarchingCubes = marchingCubes;
        UpscaleTerrain = upscaleTerrain;

        GenerateTerrainKernel = GenerateTerrainShader.FindKernel("Main");
        EmitVertexesKernel = EmitVertexes.FindKernel("EmitVertex");
        EmitTrianglesKernel = MarchingCubes.FindKernel("EmitTriangles");
        UpscaleTerrainKernel = UpscaleTerrain.FindKernel("Upscale");


        SharedUpscaledDensityMap = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.R16);

        SharedJobSizeBuffer = new(3, 4, ComputeBufferType.IndirectArguments);
        SharedJobSizeBuffer.SetData(new int[] { 1, 1, 1 });

        SharedVertexIndexMapX = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);
        SharedVertexIndexMapY = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);
        SharedVertexIndexMapZ = ComputeOperator.MakeVolume(Sector.OutputSizeInVoxels, RenderTextureFormat.RInt);



        if (Sector.OutputSizeInVoxels > 256)
            throw new InvalidOperationException("Bad voxel volume for 8bit indexes");
        SharedCellBuffer = new(Sector.TotalOutputVoxelCount, 4, ComputeBufferType.Append); //x,y,z,i = 4*(byte)
    }


    public ComputeShader GenerateTerrainShader { get; }
    public ComputeShader EmitVertexes { get; }
    public ComputeShader MarchingCubes { get; }
    public ComputeShader UpscaleTerrain { get; }


    public int GenerateTerrainKernel { get; }
    public int EmitVertexesKernel { get; }
    public int EmitTrianglesKernel { get; }
    public int UpscaleTerrainKernel { get; }



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
        void DrawBox(Vector3 lower, Vector3 upper, Color c);

    }

    public static string Ar<T>(IEnumerable<T> array) => "[" + string.Join(",", array) + "]";
    public static string Ar(IEnumerable<float> floatArray) => "[" + string.Join(",", floatArray.Select(vi => vi.ToString(CultureInfo.InvariantCulture))) + "]";
    public static string Ar(IEnumerable<double> doubleArray) => "[" + string.Join(",", doubleArray.Select(vi => vi.ToString(CultureInfo.InvariantCulture))) + "]";

    public static Vector3 V(float x) => new(x, x, x);
    public static Vector3 V(float x, float y, float z) => new(x, y, z);
    public static Vector3 V(float[] v, int offset)
    {
        try
        {
            return offset >= 0 && offset+2 < v.Length ? new(v[offset], v[offset + 1], v[offset + 2]) : throw new InvalidOperationException("Unable to access vertex offset " + offset + ". Float count is " + v.Length);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to access vertex offset " + offset + ". Float count is " + v.Length,ex);
        }
    }

    public void GenerateTerrain(Sector sector)
    {
        GenerateTerrainShader.SetTexture(GenerateTerrainKernel, "DensityVolume", sector.DensityMap);
        GenerateTerrainShader.SetTexture(GenerateTerrainKernel, "MaterialVolume", sector.MaterialMap);
        GenerateTerrainShader.SetVector("Origin", sector.Origin);
        GenerateTerrainShader.SetFloat("VoxelSize", Sector.InputVoxelSize);
        GenerateTerrainShader.SetInt("SizeInVoxels", Sector.InputSizeInVoxels);

        Dispatch(GenerateTerrainShader, GenerateTerrainKernel, Sector.InputSizeInVoxels);

    }


    public static void Profile(string name, Action act)
    {
        var watch = Stopwatch.StartNew();

        act();

        watch.Stop();
        var elapsed = watch.Elapsed;
        Debug.Log($"{name}: {elapsed}");

    }
    public static async Task Profile(string name, Func<Task> act)
    {
        var watch = Stopwatch.StartNew();

        await act();

        watch.Stop();
        var elapsed = watch.Elapsed;
        Debug.Log($"{name}: {elapsed}");
    }

    public BakeJob Compile(Sector sector, OvenSlot target)
    {
        Profile("Upscale", () =>
        {


            UpscaleTerrain.SetTexture(UpscaleTerrainKernel, "Volume", sector.DensityMap);
            UpscaleTerrain.SetVector("Origin", sector.Origin);
            UpscaleTerrain.SetInt("InputVoxelResolution", Sector.InputSizeInVoxels);
            UpscaleTerrain.SetInt("OutputVoxelResolution", Sector.OutputSizeInVoxels);
            UpscaleTerrain.SetInt("InputOutputMultiplier", Sector.OutputMultiplier);
            UpscaleTerrain.SetTexture(UpscaleTerrainKernel, "OutputDensity", SharedUpscaledDensityMap);

            Dispatch(UpscaleTerrain, UpscaleTerrainKernel, Sector.OutputSizeInVoxels);
        });


        Profile("EmitVertexes", () =>
        {
            target.SharedVertexBuffer.SetCounterValue(0);
            SharedCellBuffer.SetCounterValue(0);
#if !LOW_RESOLUTION
            EmitVertexes.SetTexture(EmitVertexesKernel, "Volume", SharedUpscaledDensityMap);
#else
        EmitVertexes.SetTexture(EmitVertexesKernel, "Volume", sector.DensityMap);
#endif
            EmitVertexes.SetVector("Origin", sector.Origin);
            EmitVertexes.SetFloat("VoxelSize", Sector.OutputVoxelSize);
            EmitVertexes.SetInt("SizeInVoxels", Sector.OutputSizeInVoxels);
            EmitVertexes.SetBuffer(EmitVertexesKernel, "VertexOut", target.SharedVertexBuffer);
            EmitVertexes.SetBuffer(EmitVertexesKernel, "CellOut", SharedCellBuffer);
            EmitVertexes.SetTexture(EmitVertexesKernel, "IndexOutMapX", SharedVertexIndexMapX);
            EmitVertexes.SetTexture(EmitVertexesKernel, "IndexOutMapY", SharedVertexIndexMapY);
            EmitVertexes.SetTexture(EmitVertexesKernel, "IndexOutMapZ", SharedVertexIndexMapZ);
            //EmitVertexes.SetBuffer(kernel, "DebugIndexOutX", DebugIndexOutX);
            //EmitVertexes.SetBuffer(kernel, "DebugIndexOutY", DebugIndexOutY);
            //EmitVertexes.SetBuffer(kernel, "DebugIndexOutZ", DebugIndexOutZ);

            Dispatch(EmitVertexes, EmitVertexesKernel, Sector.OutputSizeInVoxels);
        });

        var vertexCount = ComputeOperator.GetCountAsync(target.SharedVertexBuffer);




        Profile("EmitTriangles", () =>
        {

            ComputeBuffer.CopyCount(SharedCellBuffer, SharedJobSizeBuffer, 0);

            //if (debugOut is not null)
            //{
            //    var cnt = (await ComputeOperator.GetAllAsync<int>(SharedJobSizeBuffer, 4, 3, 0));
            //    debugOut.Log($"Size: {cnt[0]},{cnt[1]},{cnt[2]}");
            //}

            target.SharedIndexBuffer.SetCounterValue(0);
            MarchingCubes.SetBuffer(EmitTrianglesKernel, "CellTable", SharedCellBuffer);
            MarchingCubes.SetBuffer(EmitTrianglesKernel, "IndexOutBuffer", target.SharedIndexBuffer);

            MarchingCubes.SetTexture(EmitTrianglesKernel, "IndexInMapX", SharedVertexIndexMapX);
            MarchingCubes.SetTexture(EmitTrianglesKernel, "IndexInMapY", SharedVertexIndexMapY);
            MarchingCubes.SetTexture(EmitTrianglesKernel, "IndexInMapZ", SharedVertexIndexMapZ);
            MarchingCubes.DispatchIndirect(EmitTrianglesKernel, SharedJobSizeBuffer);
        });

        var triangleCount = ComputeOperator.GetCountAsync(target.SharedIndexBuffer);

            return new (target, vertexCount, triangleCount, sector);
    }
}
