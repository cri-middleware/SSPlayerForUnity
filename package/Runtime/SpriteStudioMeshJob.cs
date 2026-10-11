using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace SpriteStudio.Runtime
{
    /// <summary>
    /// Share the frame-data enum values (ss.runtime.UpdateAttributeFlags) by deriving them from the generated
    /// enum rather than from literals. Having the accounting side (SpriteStudioInternalPlayer) and the Burst Job
    /// (UpdateMeshJob) reference the same definition prevents the "edit one side and the values drift" accident
    /// (a mismatch between the vertex-count accounting and the Job).
    /// Burst inlines const, so the Job can reference it too.
    /// </summary>
    internal static class SsUpdateFlag
    {
        public const ulong AttributeVertex = (ulong)ss.runtime.UpdateAttributeFlags.AttributeVertex;
        public const ulong AttributePartColor = (ulong)ss.runtime.UpdateAttributeFlags.AttributePartColor;
        // Center vertex needed = a frame where vertex deform or part color is active (same condition as SDK core/framedata.rs).
        public const ulong NeedsCenterMask = AttributeVertex | AttributePartColor;
    }

    /// <summary>Shared constants for DrawBatch.Kind literals, derived from the generated enum (ss.runtime.DrawBatchKind).</summary>
    internal static class SsDrawBatchKind
    {
        public const byte Normal = (byte)ss.runtime.DrawBatchKind.Normal;
        public const byte Shape = (byte)ss.runtime.DrawBatchKind.Shape;
        public const byte Instance = (byte)ss.runtime.DrawBatchKind.Instance;
        public const byte Effect = (byte)ss.runtime.DrawBatchKind.Effect;
        public const byte Mesh = (byte)ss.runtime.DrawBatchKind.Mesh;
    }

    public struct DrawBatchData
    {
        public ushort StartRank;
        public ushort Count;
        public int VertexCount;
        public int IndexCount;
        public byte Kind;
        // ssab draw BlendType (0..7) for Normal/Shape/Mesh. Carried per batch so the mesh job can pack
        // a per-vertex "is additive" flag for the Canvas single-material PMA path (a=0 trick).
        public byte BlendType;
        public byte EffectBlend;
        public uint EffectParticleOffset;
        public uint EffectQuadCount;
        public int EffectPartIndex;
    }

    // Effect draw command (the draw range for one emitter/cellmap).
    // Filled by copying values from the generated accessor ss.runtime.EffectDrawCommand.
    public struct EffectDrawCommandData
    {
        public uint CellmapHash;
        public uint QuadCount;
        public uint ParticleOffset; // slot-local quad offset
        public byte Blend;          // 0=Mix, 1=Add
    }

    // Holds one effect part's draw plan as position info into the merged buffers (_effectVerts, etc.).
    // The geometry itself has already been copied into the merged NativeArrays.
    public struct EffectDrawPlanData
    {
        public int CommandStart;  // start index within _effectCommands
        public int CommandCount;  // command count (0 = no draw)
        public int QuadBase;      // global quad offset within the merged geometry
    }

    public unsafe struct InstanceMeshPointer
    {
        public SsVertex* Vertices;
        public uint* Indices;
        public int VertexCount;
        public int IndexCount;
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

    /// <summary>
    /// The Canvas upload's conversion (SpriteStudioMeshUploader.ApplyCanvas): the packed vertex stream split
    /// into the per-channel arrays the high-level Mesh API takes, and the indices narrowed to 16 bits. The
    /// vertex colour becomes the tint times the part's alpha, and the part colour moves to TEXCOORD2.
    /// IndexCount 0 leaves the narrowed indices as they were.
    /// </summary>
    [BurstCompile(CompileSynchronously = false)]
    internal struct CanvasStreamsJob : IJob
    {
        [ReadOnly] public NativeArray<SsVertex> Vertices;
        [ReadOnly] public NativeArray<uint> Indices;
        public Color32 Tint;
        public int VertexCount;
        public int IndexCount;

        [WriteOnly] public NativeArray<Vector3> Positions;
        [WriteOnly] public NativeArray<Color32> Colors;
        [WriteOnly] public NativeArray<Vector2> Uv0;
        [WriteOnly] public NativeArray<Vector4> Uv1;
        [WriteOnly] public NativeArray<Vector3> Uv2;
        [WriteOnly] public NativeArray<ushort> Indices16;

        public void Execute()
        {
            const float inv255 = 1f / 255f;
            for (int i = 0; i < VertexCount; i++)
            {
                var v = Vertices[i];
                Positions[i] = v.Position;
                Colors[i] = new Color32(Tint.r, Tint.g, Tint.b, (byte)((Tint.a * v.Color.a) / 255));
                Uv0[i] = v.TexCoord0;
                Uv1[i] = v.TexCoord1;
                Uv2[i] = new Vector3(v.Color.r * inv255, v.Color.g * inv255, v.Color.b * inv255);
            }
            for (int i = 0; i < IndexCount; i++) Indices16[i] = (ushort)Indices[i];
        }
    }

    [BurstCompile(CompileSynchronously = false)]
    public unsafe struct UpdateMeshJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public float* WorldMatrices;
        [NativeDisableUnsafePtrRestriction] public float* LocalUvs;
        [NativeDisableUnsafePtrRestriction] public float* LocalVertices;

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
        // Merged effect geometry (all effect parts concatenated; off is the global quad offset).
        [ReadOnly] public NativeArray<float> EffectVerts;
        [ReadOnly] public NativeArray<float> EffectUvs;
        [ReadOnly] public NativeArray<float> EffectColors;
        [ReadOnly] public NativeArray<int> EffectIndices;
        [ReadOnly] public NativeArray<InstanceMeshPointer> InstanceMeshes;

        public NativeArray<SsVertex> Vertices;
        public NativeArray<uint> Indices;
        // Mesh bounds (element 0=min, 1=max). Aggregated at the end of Execute.
        public NativeArray<Vector3> Bounds;

        // Input buffers may be over-allocated grow-only, so iterate by logical count
        public int BatchCount;
        public int PartCount;
        public int PartColorCount;

        // Root transform folded into every final vertex position, from SpriteStudioInternalPlayer.RootScale /
        // VertexOffset. Scale converts pixel-space world vertices into Unity world units (= 1 / PixelsPerUnit),
        // negated per axis when that axis is flipped; Offset shifts the result and is already in Unity units.
        // Neither is applied to UVs. Folding them in here rather than onto the GameObject's Transform keeps
        // flip / offset from dragging the player's children and colliders along.
        public Vector2 RootScale;
        public Vector2 RootOffset;

        public void Execute()
        {
            int vbase = 0;
            int ibase = 0;

            for (int i = 0; i < BatchCount; i++)
            {
                var batch = DrawBatches[i];
                if (batch.Kind == SsDrawBatchKind.Effect)
                {
                    BuildEffect(ref batch, ref vbase, ref ibase);
                    continue;
                }
                else if (batch.Kind == SsDrawBatchKind.Instance)
                {
                    BuildInstance(ref batch, ref vbase, ref ibase);
                    continue;
                }

                for (int k = 0; k < batch.Count; k++)
                {
                    int rank = batch.StartRank + k;
                    int p_idx = DrawOrder[rank];
                    if (p_idx < 0 || p_idx >= PartCount) continue;

                    float isAdditive = (batch.BlendType == 2) ? 1f : 0f; // Add(2) -> a=0 PMA trick (Canvas)
                    if (batch.Kind == SsDrawBatchKind.Normal)
                    {
                        BuildNormal(p_idx, isAdditive, ref vbase, ref ibase);
                    }
                    else if (batch.Kind == SsDrawBatchKind.Shape)
                    {
                        BuildShape(p_idx, isAdditive, ref vbase, ref ibase);
                    }
                    else if (batch.Kind == SsDrawBatchKind.Mesh)
                    {
                        BuildMesh(p_idx, isAdditive, ref vbase, ref ibase);
                    }
                }
            }

            // Aggregate the vertices' min/max to derive the mesh bounds.
            if (Bounds.Length >= 2)
            {
                if (vbase <= 0)
                {
                    Bounds[0] = Vector3.zero;
                    Bounds[1] = Vector3.zero;
                }
                else
                {
                    float3 mn = Vertices[0].Position;
                    float3 mx = mn;
                    for (int v = 1; v < vbase; v++)
                    {
                        float3 p = Vertices[v].Position;
                        mn = math.min(mn, p);
                        mx = math.max(mx, p);
                    }
                    Bounds[0] = (Vector3)mn;
                    Bounds[1] = (Vector3)mx;
                }
            }
        }

        private void BuildEffect(ref DrawBatchData batch, ref int vbase, ref int ibase)
        {
            int p_idx = batch.EffectPartIndex;
            uint qc = batch.EffectQuadCount;
            if (qc == 0 || EffectIndices.Length == 0) return;

            var part = PartStates[p_idx];
            byte alphaByte = (byte)(math.clamp(part.Alpha, 0f, 1f) * 255f);

            float* draw_m = WorldMatrices + p_idx * 16;

            uint off = batch.EffectParticleOffset; // global quad offset within the merged geometry
            int vertStart = (int)off * 4;
            int indStart = (int)off * 6;

            // ss.format.BlendType: Mix=0, Add=2 (shader texcoord1.y)
            int blend_idx = batch.EffectBlend == 1 ? 2 : 0;
            // Per-vertex additive flag (TexCoord1.w) for the Canvas single-material a=0 trick.
            float isAdditive = batch.EffectBlend == 1 ? 1f : 0f;

            for (int q = 0; q < qc; q++)
            {
                int qVertBase = vertStart + q * 4;
                for (int v = 0; v < 4; v++)
                {
                    int vi = qVertBase + v;

                    // Effect vertices have stride 3 (X, Y, Z). Z is unused.
                    float lx = EffectVerts[vi * 3 + 0];
                    float ly = EffectVerts[vi * 3 + 1];

                    float ux = EffectUvs[vi * 2 + 0];
                    float uy = EffectUvs[vi * 2 + 1];

                    byte r = (byte)(math.clamp(EffectColors[vi * 4 + 0], 0f, 1f) * 255f);
                    byte g = (byte)(math.clamp(EffectColors[vi * 4 + 1], 0f, 1f) * 255f);
                    byte b = (byte)(math.clamp(EffectColors[vi * 4 + 2], 0f, 1f) * 255f);
                    byte a = (byte)(math.clamp(EffectColors[vi * 4 + 3], 0f, 1f) * 255f);
                    a = (byte)((a * alphaByte) / 255);

                    Vertices[vbase + q * 4 + v] = new SsVertex
                    {
                        Position = new Vector3(
                            (draw_m[0] * lx + draw_m[4] * ly + draw_m[12]) * RootScale.x + RootOffset.x,
                            (draw_m[1] * lx + draw_m[5] * ly + draw_m[13]) * RootScale.y + RootOffset.y,
                            0f),
                        Color = new Color32(r, g, b, a),
                        TexCoord0 = new Vector2(ux, uy),
                        TexCoord1 = new Vector4(0f, blend_idx, 1f, isAdditive) // x=rate(0=tex), y=blend_idx, z=1(normalized), w=isAdditive
                    };
                }

                int qIndBase = indStart + q * 6;
                for (int i = 0; i < 6; i++)
                {
                    int planIdx = EffectIndices[qIndBase + i];
                    Indices[ibase + q * 6 + i] = (uint)(vbase + planIdx);
                }
            }
            vbase += (int)qc * 4;
            ibase += (int)qc * 6;
        }

        private unsafe void BuildInstance(ref DrawBatchData batch, ref int vbase, ref int ibase)
        {
            int p_idx = batch.EffectPartIndex;
            var meshPtr = InstanceMeshes[p_idx];
            if (meshPtr.Vertices == null) return;
            
            var part = PartStates[p_idx];
            float partAlpha = part.Alpha;
            byte alphaByte = (byte)(math.clamp(partAlpha, 0f, 1f) * 255f);
            
            float* draw_m = WorldMatrices + p_idx * 16;
            
            int vc = meshPtr.VertexCount;
            int ic = meshPtr.IndexCount;
            
            for (int v = 0; v < vc; v++)
            {
                var vert = meshPtr.Vertices[v];
                
                float lx = vert.Position.x;
                float ly = vert.Position.y;
                vert.Position.x = (draw_m[0] * lx + draw_m[4] * ly + draw_m[12]) * RootScale.x + RootOffset.x;
                vert.Position.y = (draw_m[1] * lx + draw_m[5] * ly + draw_m[13]) * RootScale.y + RootOffset.y;
                
                byte a = vert.Color.a;
                vert.Color.a = (byte)((a * alphaByte) / 255);
                
                Vertices[vbase + v] = vert;
            }
            
            for (int i = 0; i < ic; i++)
            {
                Indices[ibase + i] = (uint)(meshPtr.Indices[i] + (uint)vbase);
            }
            
            vbase += vc;
            ibase += ic;
        }

        private void BuildNormal(int p_idx, float isAdditive, ref int vbase, ref int ibase)
        {
            var part = PartStates[p_idx];
            bool needsCenter = (part.UpdateFlag & SsUpdateFlag.NeedsCenterMask) != 0;
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

            if ((part.UpdateFlag & SsUpdateFlag.AttributePartColor) != 0 && part.PartColorIndex >= 0 && part.PartColorIndex < PartColorCount)
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
                        (draw_m[0] * out_x[i] + draw_m[4] * out_y[i] + draw_m[12]) * RootScale.x + RootOffset.x,
                        (draw_m[1] * out_x[i] + draw_m[5] * out_y[i] + draw_m[13]) * RootScale.y + RootOffset.y,
                        0f),
                    Color = corner_colors[i],
                    TexCoord0 = new Vector2(part_uvs[i * 2], part_uvs[i * 2 + 1]),
                    TexCoord1 = new Vector4(corner_rates[i], blend_idx, 0f, isAdditive)
                };
            }

            if (needsCenter)
            {
                Vertices[vbase + 4] = new SsVertex
                {
                    Position = new Vector3(
                        (draw_m[0] * out_x[4] + draw_m[4] * out_y[4] + draw_m[12]) * RootScale.x + RootOffset.x,
                        (draw_m[1] * out_x[4] + draw_m[5] * out_y[4] + draw_m[13]) * RootScale.y + RootOffset.y,
                        0f),
                    Color = new Color32(
                        (byte)((corner_colors[0].r + corner_colors[1].r + corner_colors[2].r + corner_colors[3].r) / 4),
                        (byte)((corner_colors[0].g + corner_colors[1].g + corner_colors[2].g + corner_colors[3].g) / 4),
                        (byte)((corner_colors[0].b + corner_colors[1].b + corner_colors[2].b + corner_colors[3].b) / 4),
                        (byte)((corner_colors[0].a + corner_colors[1].a + corner_colors[2].a + corner_colors[3].a) / 4)),
                    TexCoord0 = new Vector2(part_uvs[8], part_uvs[9]),
                    TexCoord1 = new Vector4((corner_rates[0] + corner_rates[1] + corner_rates[2] + corner_rates[3]) * 0.25f, blend_idx, 0f, isAdditive)
                };

                // Pentagon indices: 0,1,4, 1,3,4, 3,2,4, 2,0,4
                ushort* p = stackalloc ushort[12] { 0, 1, 4, 1, 3, 4, 3, 2, 4, 2, 0, 4 };
                for (int j = 0; j < 12; j++) Indices[ibase + j] = (uint)(vbase + p[j]);
                ibase += 12;
            }
            else
            {
                // Quad indices: 0,1,2, 1,3,2
                ushort* q = stackalloc ushort[6] { 0, 1, 2, 1, 3, 2 };
                for (int j = 0; j < 6; j++) Indices[ibase + j] = (uint)(vbase + q[j]);
                ibase += 6;
            }

            vbase += vertCount;
        }

        private void BuildShape(int p_idx, float isAdditive, ref int vbase, ref int ibase)
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

            if ((part.UpdateFlag & SsUpdateFlag.AttributePartColor) != 0 && part.PartColorIndex >= 0 && part.PartColorIndex < PartColorCount)
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

                // The weights sum to 1, but FP error can nudge it slightly past 255, so clamp as the other paths (effect/normal) do.
                byte r = (byte)math.clamp(corner_colors[0].r * wLT + corner_colors[1].r * wRT + corner_colors[2].r * wLB + corner_colors[3].r * wRB, 0f, 255f);
                byte g = (byte)math.clamp(corner_colors[0].g * wLT + corner_colors[1].g * wRT + corner_colors[2].g * wLB + corner_colors[3].g * wRB, 0f, 255f);
                byte b = (byte)math.clamp(corner_colors[0].b * wLT + corner_colors[1].b * wRT + corner_colors[2].b * wLB + corner_colors[3].b * wRB, 0f, 255f);
                byte a = (byte)math.clamp(corner_colors[0].a * wLT + corner_colors[1].a * wRT + corner_colors[2].a * wLB + corner_colors[3].a * wRB, 0f, 255f);
                
                float v_rate = corner_rates[0] * wLT + corner_rates[1] * wRT + corner_rates[2] * wLB + corner_rates[3] * wRB;

                Vertices[vbase + i] = new SsVertex
                {
                    Position = new Vector3(
                        (draw_m[0] * vx + draw_m[4] * vy + draw_m[12]) * RootScale.x + RootOffset.x,
                        (draw_m[1] * vx + draw_m[5] * vy + draw_m[13]) * RootScale.y + RootOffset.y,
                        0f),
                    Color = new Color32(r, g, b, a),
                    TexCoord0 = new Vector2(0f, 0f),
                    TexCoord1 = new Vector4(v_rate, blend_idx, 0f, isAdditive)
                };
            }

            int tri_count = part_shape_count - 2;
            for (int i = 0; i < tri_count; i++)
            {
                Indices[ibase + i * 3 + 0] = (uint)(vbase + 0);
                Indices[ibase + i * 3 + 1] = (uint)(vbase + i + 1);
                Indices[ibase + i * 3 + 2] = (uint)(vbase + i + 2);
            }

            vbase += part_shape_count;
            ibase += tri_count * 3;
        }

        private void BuildMesh(int p_idx, float isAdditive, ref int vbase, ref int ibase)
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

            if ((part.UpdateFlag & SsUpdateFlag.AttributePartColor) != 0 && part.PartColorIndex >= 0 && part.PartColorIndex < PartColorCount)
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
                    Position = new Vector3(MeshVerticesX[srcIdx] * RootScale.x + RootOffset.x, MeshVerticesY[srcIdx] * RootScale.y + RootOffset.y, 0f),
                    Color = meshColor,
                    TexCoord0 = new Vector2(MeshUvs[srcIdx * 2], MeshUvs[srcIdx * 2 + 1]),
                    TexCoord1 = new Vector4(meshRate, blendIdx, 0f, isAdditive)
                };
            }

            for (int j = 0; j < indexCount; j++)
            {
                int srcIdx = (int)(i0 + j);
                Indices[ibase + j] = (uint)(vbase + MeshIndices[srcIdx]);
            }

            vbase += vertCount;
            ibase += indexCount;
        }
    }
}
