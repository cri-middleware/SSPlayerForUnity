using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    public struct DrawBatchData
    {
        public ushort StartRank;
        public ushort Count;
        public ushort VertexCount;
        public ushort IndexCount;
        public byte Kind;
    }

    public struct PartStateData
    {
        public ulong UpdateFlag;
        public float Alpha;
        public short PartColorIndex;
    }

    public struct CornerColorData
    {
        public Color32 Rgba;
        public float Rate;
    }

    public struct PartColorData
    {
        public byte BlendType;
        public CornerColorData Lt;
        public CornerColorData Rt;
        public CornerColorData Lb;
        public CornerColorData Rb;
    }

    public struct SsVertex
    {
        public Vector3 Position;
        public Color32 Color;
        public Vector2 TexCoord0;
        public Vector4 TexCoord1; // [rate, blend_idx, pma_flag, reserved]
    }

    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct UpdateMeshJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public float* WorldMatrices;
        [NativeDisableUnsafePtrRestriction] public float* LocalUvs;
        [NativeDisableUnsafePtrRestriction] public float* LocalVertices;
        [NativeDisableUnsafePtrRestriction] public float* CellMeta;

        [NativeDisableUnsafePtrRestriction] public float* ShapeVertices;
        [NativeDisableUnsafePtrRestriction] public float* ShapeBoxCoords;
        [NativeDisableUnsafePtrRestriction] public int* ShapeVertexCounts;

        [NativeDisableUnsafePtrRestriction] public float* MeshVerticesX;
        [NativeDisableUnsafePtrRestriction] public float* MeshVerticesY;
        [NativeDisableUnsafePtrRestriction] public uint* MeshVertexOffsets;
        [NativeDisableUnsafePtrRestriction] public float* MeshUvs;
        [NativeDisableUnsafePtrRestriction] public int* MeshIndices;
        [NativeDisableUnsafePtrRestriction] public uint* MeshIndexOffsets;

        [ReadOnly] public NativeArray<DrawBatchData> DrawBatches;
        [ReadOnly] public NativeArray<ushort> DrawOrder;
        [ReadOnly] public NativeArray<PartStateData> PartStates;
        [ReadOnly] public NativeArray<PartColorData> PartColors;

        public NativeArray<SsVertex> Vertices;
        public NativeArray<ushort> Indices;

        // 入力バッファは grow-only で過剰確保されうるため、走査は論理件数で行う
        public int BatchCount;
        public int PartCount;
        public int PartColorCount;

        public void Execute()
        {
            int vbase = 0;
            int ibase = 0;

            for (int i = 0; i < BatchCount; i++)
            {
                var batch = DrawBatches[i];
                for (int k = 0; k < batch.Count; k++)
                {
                    int rank = batch.StartRank + k;
                    int p_idx = DrawOrder[rank];
                    if (p_idx < 0 || p_idx >= PartCount) continue;

                    // DrawBatchKind: Normal=0, Shape=1, Instance=2, Effect=3, Mesh=4, Text=5, Nines=6, Mask=7
                    if (batch.Kind == 0) // Normal
                    {
                        BuildNormal(p_idx, ref vbase, ref ibase);
                    }
                    else if (batch.Kind == 1) // Shape
                    {
                        BuildShape(p_idx, ref vbase, ref ibase);
                    }
                    else if (batch.Kind == 4) // Mesh
                    {
                        BuildMesh(p_idx, ref vbase, ref ibase);
                    }
                }
            }
        }

        private void BuildNormal(int p_idx, ref int vbase, ref int ibase)
        {
            var part = PartStates[p_idx];
            bool needsCenter = (part.UpdateFlag & (0x10 | 0x200)) != 0; // AttributeVertex | AttributePartColor
            int vertCount = needsCenter ? 5 : 4;

            // Fetch pointers for this part
            // LOCAL_VERTICES_STRIDE = 10 floats/part: [lt.x,lt.y, rt.x,rt.y, lb.x,lb.y, rb.x,rb.y, center.x,center.y]
            float* part_verts = LocalVertices + p_idx * 10;
            float* part_uvs = LocalUvs + p_idx * 10;
            float* draw_m = WorldMatrices + p_idx * 16;

            float* out_x = stackalloc float[5] { part_verts[0], part_verts[2], part_verts[4], part_verts[6], part_verts[8] };
            float* out_y = stackalloc float[5] { part_verts[1], part_verts[3], part_verts[5], part_verts[7], part_verts[9] };

            Color32* corner_colors = stackalloc Color32[4] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            float* corner_rates = stackalloc float[4] { 0f, 0f, 0f, 0f };
            int blend_idx = 0;

            if ((part.UpdateFlag & 0x200) != 0 && part.PartColorIndex >= 0 && part.PartColorIndex < PartColorCount)
            {
                var pc = PartColors[part.PartColorIndex];
                corner_colors[0] = pc.Lt.Rgba; corner_rates[0] = pc.Lt.Rate;
                corner_colors[1] = pc.Rt.Rgba; corner_rates[1] = pc.Rt.Rate;
                corner_colors[2] = pc.Lb.Rgba; corner_rates[2] = pc.Lb.Rate;
                corner_colors[3] = pc.Rb.Rgba; corner_rates[3] = pc.Rb.Rate;
                blend_idx = pc.BlendType;
                if (blend_idx > 3) blend_idx = 0;
            }

            // Apply part alpha
            byte alphaByte = (byte)(math.clamp(part.Alpha, 0f, 1f) * 255f);
            for (int i = 0; i < 4; i++)
            {
                corner_colors[i].a = (byte)((corner_colors[i].a * alphaByte) / 255);
            }

            for (int i = 0; i < 4; i++)
            {
                Vertices[vbase + i] = new SsVertex
                {
                    Position = new Vector3(
                        draw_m[0] * out_x[i] + draw_m[4] * out_y[i] + draw_m[12],
                        draw_m[1] * out_x[i] + draw_m[5] * out_y[i] + draw_m[13],
                        0f),
                    Color = corner_colors[i],
                    TexCoord0 = new Vector2(part_uvs[i * 2], part_uvs[i * 2 + 1]),
                    TexCoord1 = new Vector4(corner_rates[i], blend_idx, 0f, 0f)
                };
            }

            if (needsCenter)
            {
                Vertices[vbase + 4] = new SsVertex
                {
                    Position = new Vector3(
                        draw_m[0] * out_x[4] + draw_m[4] * out_y[4] + draw_m[12],
                        draw_m[1] * out_x[4] + draw_m[5] * out_y[4] + draw_m[13],
                        0f),
                    Color = new Color32(
                        (byte)((corner_colors[0].r + corner_colors[1].r + corner_colors[2].r + corner_colors[3].r) / 4),
                        (byte)((corner_colors[0].g + corner_colors[1].g + corner_colors[2].g + corner_colors[3].g) / 4),
                        (byte)((corner_colors[0].b + corner_colors[1].b + corner_colors[2].b + corner_colors[3].b) / 4),
                        (byte)((corner_colors[0].a + corner_colors[1].a + corner_colors[2].a + corner_colors[3].a) / 4)),
                    TexCoord0 = new Vector2(part_uvs[8], part_uvs[9]),
                    TexCoord1 = new Vector4((corner_rates[0] + corner_rates[1] + corner_rates[2] + corner_rates[3]) * 0.25f, blend_idx, 0f, 0f)
                };

                // Pentagon indices: 0,1,4, 1,3,4, 3,2,4, 2,0,4
                ushort* p = stackalloc ushort[12] { 0, 1, 4, 1, 3, 4, 3, 2, 4, 2, 0, 4 };
                for (int j = 0; j < 12; j++) Indices[ibase + j] = (ushort)(vbase + p[j]);
                ibase += 12;
            }
            else
            {
                // Quad indices: 0,1,2, 1,3,2
                ushort* q = stackalloc ushort[6] { 0, 1, 2, 1, 3, 2 };
                for (int j = 0; j < 6; j++) Indices[ibase + j] = (ushort)(vbase + q[j]);
                ibase += 6;
            }

            vbase += vertCount;
        }

        private void BuildShape(int p_idx, ref int vbase, ref int ibase)
        {
            int part_shape_count = ShapeVertexCounts[p_idx];
            if (part_shape_count < 3) return;

            float* part_shape_verts = ShapeVertices + p_idx * 24;
            float* part_shape_box_coords = ShapeBoxCoords + p_idx * 24;
            float* draw_m = WorldMatrices + p_idx * 16;

            var part = PartStates[p_idx];
            float partAlpha = part.Alpha;
            byte alphaByte = (byte)(math.clamp(partAlpha, 0f, 1f) * 255f);

            Color32* corner_colors = stackalloc Color32[4] {
                new Color32(255, 255, 255, alphaByte),
                new Color32(255, 255, 255, alphaByte),
                new Color32(255, 255, 255, alphaByte),
                new Color32(255, 255, 255, alphaByte)
            };
            float* corner_rates = stackalloc float[4] { 1f, 1f, 1f, 1f };
            int blend_idx = 0;

            if ((part.UpdateFlag & 0x200) != 0 && part.PartColorIndex >= 0 && part.PartColorIndex < PartColorCount)
            {
                var pc = PartColors[part.PartColorIndex];
                corner_colors[0] = pc.Lt.Rgba;
                corner_colors[1] = pc.Rt.Rgba;
                corner_colors[2] = pc.Lb.Rgba;
                corner_colors[3] = pc.Rb.Rgba;
                
                for (int i = 0; i < 4; i++)
                {
                    corner_colors[i].a = (byte)((corner_colors[i].a * alphaByte) / 255);
                }
            }

            for (int i = 0; i < part_shape_count; i++)
            {
                float vx = part_shape_verts[i * 2];
                float vy = part_shape_verts[i * 2 + 1];
                float fx = part_shape_box_coords[i * 2];
                float fy = part_shape_box_coords[i * 2 + 1];
                
                float wLT = (1f - fx) * (1f - fy);
                float wRT = fx * (1f - fy);
                float wLB = (1f - fx) * fy;
                float wRB = fx * fy;

                byte r = (byte)(corner_colors[0].r * wLT + corner_colors[1].r * wRT + corner_colors[2].r * wLB + corner_colors[3].r * wRB);
                byte g = (byte)(corner_colors[0].g * wLT + corner_colors[1].g * wRT + corner_colors[2].g * wLB + corner_colors[3].g * wRB);
                byte b = (byte)(corner_colors[0].b * wLT + corner_colors[1].b * wRT + corner_colors[2].b * wLB + corner_colors[3].b * wRB);
                byte a = (byte)(corner_colors[0].a * wLT + corner_colors[1].a * wRT + corner_colors[2].a * wLB + corner_colors[3].a * wRB);
                
                float v_rate = corner_rates[0] * wLT + corner_rates[1] * wRT + corner_rates[2] * wLB + corner_rates[3] * wRB;

                Vertices[vbase + i] = new SsVertex
                {
                    Position = new Vector3(
                        draw_m[0] * vx + draw_m[4] * vy + draw_m[12],
                        draw_m[1] * vx + draw_m[5] * vy + draw_m[13],
                        0f),
                    Color = new Color32(r, g, b, a),
                    TexCoord0 = new Vector2(0f, 0f),
                    TexCoord1 = new Vector4(v_rate, blend_idx, 0f, 0f)
                };
            }

            int tri_count = part_shape_count - 2;
            for (int i = 0; i < tri_count; i++)
            {
                Indices[ibase + i * 3 + 0] = (ushort)(vbase + 0);
                Indices[ibase + i * 3 + 1] = (ushort)(vbase + i + 1);
                Indices[ibase + i * 3 + 2] = (ushort)(vbase + i + 2);
            }

            vbase += part_shape_count;
            ibase += tri_count * 3;
        }

        private void BuildMesh(int p_idx, ref int vbase, ref int ibase)
        {
            uint v0 = MeshVertexOffsets[p_idx];
            uint v1 = MeshVertexOffsets[p_idx + 1];
            if (v1 <= v0) return;
            int vertCount = (int)(v1 - v0);

            uint i0 = MeshIndexOffsets[p_idx];
            uint i1 = MeshIndexOffsets[p_idx + 1];
            if (i1 <= i0 || (i1 - i0) % 3 != 0) return;
            int indexCount = (int)(i1 - i0);

            var part = PartStates[p_idx];
            float partAlpha = part.Alpha;
            byte alphaByte = (byte)(math.clamp(partAlpha, 0f, 1f) * 255f);
            
            Color32 meshColor = new Color32(255, 255, 255, alphaByte);
            float meshRate = 0f;
            int blendIdx = 0;

            if ((part.UpdateFlag & 0x200) != 0 && part.PartColorIndex >= 0 && part.PartColorIndex < PartColorCount)
            {
                var pc = PartColors[part.PartColorIndex];
                meshColor = pc.Lt.Rgba;
                meshColor.a = (byte)((meshColor.a * alphaByte) / 255);
                meshRate = pc.Lt.Rate;
                blendIdx = pc.BlendType;
                if (blendIdx > 3) blendIdx = 0;
            }

            for (int j = 0; j < vertCount; j++)
            {
                int srcIdx = (int)(v0 + j);
                Vertices[vbase + j] = new SsVertex
                {
                    Position = new Vector3(MeshVerticesX[srcIdx], MeshVerticesY[srcIdx], 0f),
                    Color = meshColor,
                    TexCoord0 = new Vector2(MeshUvs[srcIdx * 2], MeshUvs[srcIdx * 2 + 1]),
                    TexCoord1 = new Vector4(meshRate, blendIdx, 0f, 0f)
                };
            }

            for (int j = 0; j < indexCount; j++)
            {
                int srcIdx = (int)(i0 + j);
                Indices[ibase + j] = (ushort)(vbase + MeshIndices[srcIdx]);
            }

            vbase += vertCount;
            ibase += indexCount;
        }
    }
}
