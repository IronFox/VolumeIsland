//#define VERTEX_ONLY

//#define LOW_RESOLUTION

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

#nullable enable

public class IslandMain : MonoBehaviour, ComputeKernel.IDebugOut
{
    //public ComputeShader shader;
    public ComputeShader? generateTerrain;
    public ComputeShader? upscaleTerrain;
    public ComputeShader? emitVertexes;
    public ComputeShader? marchingCubes;
    public ComputeShader? compact;
    public RenderTexture? outTarget;
    public Material? material;

    public GameObject? slicePrefab;



    public class VertexLayout
    {
        public const int PositionSizeFloats = 3;
        public const int NormalSizeFloats = 3;


        public const int SizeFloats = PositionSizeFloats + NormalSizeFloats;

        public const int SizeBytes = SizeFloats * 4;
    }










    void OnDestroy()
    {
        kernel?.Dispose();
        sector?.Dispose();
    }



    private static Vector3 V(float x) => new(x, x, x);
    private static Vector3 V(float x, float y, float z) => new(x, y, z);
    private static Vector3 V(float[] v, int offset) => new(v[offset], v[offset + 1], v[offset+2]);

    private static void DebugDraw(Vector3 v0, Vector3 v1, Color c)
    {
        Debug.DrawLine(v0, v1, c, 1e20f);
    }
    private static void DebugDrawPoint(float x, float y, float z, Color c, float size = 0.1f)
    {
        DebugDraw(new(x - size, y, z), new(x + size, y, z), c);
        DebugDraw(new(x, y - size, z), new(x, y + size, z), c);
        DebugDraw(new(x, y, z - size), new(x, y, z + size), c);

    }

    private static string Ar<T>(IEnumerable<T> i) => "["+string.Join(",", i)+"]";

    

    Sector? sector;
    ComputeKernel? kernel;


    // Start is called before the first frame update
    void Start()
    {
        sector = new Sector(Vector3.zero);
        kernel = new ComputeKernel(
            generateTerrain: generateTerrain, 
            emitVertexes: emitVertexes, 
            marchingCubes: marchingCubes, 
            compact: compact, 
            upscaleTerrain: upscaleTerrain
            );

        kernel.GenerateTerrain(sector);

        _ = kernel.CompileAsync(sector, debugOut:null);


        //var idxReq = AsyncGPUReadback.Request(SharedIndexBuffer, 4 * 3 * Math.Min(10,numT*3), 0);
        //idxReq.WaitForCompletion();
        //outIndexData = idxReq.GetData<int>().ToArray();

//        if (slicePrefab is not null)
//        {
//            for (int i = 0; i < Sector.OutputSizeInVoxels; i++)
//            {
//                var slice = Instantiate(slicePrefab, transform);
//                slice.transform.localScale = new Vector3(10, 10, 10);
//                slice.transform.localPosition = new Vector3(0, 0, (float)i / (Sector.OutputSizeInVoxels-1) * 10);
//                var mesh = slice.GetComponentInChildren<MeshRenderer>();
//                if (mesh is not null)
//                {
//                    mesh.material.SetFloat("_Slice", (float)i / (Sector.OutputSizeInVoxels-1));
//                    mesh.material.SetFloat("_SizeInVoxels", Sector.OutputSizeInVoxels);
//#if !LOW_RESOLUTION
//                    mesh.material.SetTexture("_MainTex", SharedUpscaledDensityMap);
//#else
//                    mesh.material.SetTexture("_MainTex", sector.DensityMap);

//#endif
//                }


//            }
//        }

    }

    // Update is called once per frame
    void Update()
    {
        sector?.Render(material ?? throw new ArgumentNullException(nameof(material)));

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

    public void Log(string msg)
    {
        Debug.Log(msg);
    }

    public void DrawPoint(float x, float y, float z, Color c)
    {
        DebugDrawPoint(x, y, z, c);
    }

    public void DrawLine(Vector3 p0, Vector3 p1, Color c)
    {
        DebugDraw(p0, p1, c);
    }
    public void DrawBox(Vector3 p0, Vector3 p1, Color c)
    {
        DebugDraw(p0, new (p1.x,p0.y,p0.z), c);
        DebugDraw(p0, new (p0.x,p1.y,p0.z), c);
        DebugDraw(p0, new (p0.x,p0.y,p1.z), c);
        DebugDraw(new (p0.x,p1.y,p1.z), p1, c);
        DebugDraw(new (p1.x,p0.y,p1.z), p1, c);
        DebugDraw(new (p1.x,p1.y,p0.z), p1, c);

        DebugDraw(new (p1.x,p1.y,p0.z), new(p1.x, p0.y, p0.z), c);
        DebugDraw(new (p1.x,p1.y,p0.z), new(p0.x, p1.y, p0.z), c);
        DebugDraw(new (p1.x,p0.y,p1.z), new(p1.x, p0.y, p0.z), c);
        DebugDraw(new (p1.x,p0.y,p1.z), new(p0.x, p0.y, p1.z), c);
        DebugDraw(new (p0.x,p1.y,p1.z), new(p0.x, p1.y, p0.z), c);
        DebugDraw(new (p0.x,p1.y,p1.z), new(p0.x, p0.y, p1.z), c);
    }
}
