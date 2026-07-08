using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace WaterSFX
{
    public class WaterSFXSettings : ModSettings
    {
        public bool silenceUnderMusic;
        public bool disableDucking;
        public bool silenceUnderMusicLava;
        public bool disableDuckingLava;

        public override void ExposeData()
        {
            Scribe_Values.Look<bool>(ref this.silenceUnderMusic, "silenceUnderMusic", false, false);
            Scribe_Values.Look<bool>(ref this.disableDucking, "disableDucking", false, false);
            Scribe_Values.Look<bool>(ref this.silenceUnderMusicLava, "silenceUnderMusicLava", false, false);
            Scribe_Values.Look<bool>(ref this.disableDuckingLava, "disableDuckingLava", false, false);
        }
    }

    public class WaterSFXMod : Mod
    {
        public static WaterSFXSettings Settings;

        private static readonly Color NoteColor = new Color32(73, 214, 183, 255);

        public WaterSFXMod(ModContentPack content) : base(content)
        {
            Settings = this.GetSettings<WaterSFXSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            Color prevColor = GUI.color;
            GUI.color = NoteColor;
            listing.Label("If no options are checked the mod will automatically reduce, but not disable, its effects during music tracks.");
            GUI.color = prevColor;
            listing.Gap(6f);
            bool wasSilence = Settings.silenceUnderMusic;
            listing.CheckboxLabeled("Silence Water While Music Plays", ref Settings.silenceUnderMusic, null, 0f, 1f);
            if (Settings.silenceUnderMusic && !wasSilence)
            {
                Settings.disableDucking = false;
            }
            bool wasDisable = Settings.disableDucking;
            listing.CheckboxLabeled("Disable Audio Ducking For Water", ref Settings.disableDucking, null, 0f, 1f);
            if (Settings.disableDucking && !wasDisable)
            {
                Settings.silenceUnderMusic = false;
            }
            bool wasSilenceLava = Settings.silenceUnderMusicLava;
            listing.CheckboxLabeled("Silence Lava While Music Plays", ref Settings.silenceUnderMusicLava, null, 0f, 1f);
            if (Settings.silenceUnderMusicLava && !wasSilenceLava)
            {
                Settings.disableDuckingLava = false;
            }
            bool wasDisableLava = Settings.disableDuckingLava;
            listing.CheckboxLabeled("Disable Audio Ducking For Lava", ref Settings.disableDuckingLava, null, 0f, 1f);
            if (Settings.disableDuckingLava && !wasDisableLava)
            {
                Settings.silenceUnderMusicLava = false;
            }
            listing.End();
        }

        public override string SettingsCategory()
        {
            return "LiquidSFX";
        }
    }

    public class MapComponent_WaterSFX : MapComponent
    {
        private const int Coast = 1;
        private const int River = 2;
        private const int Still = 3;
        private const int Marsh = 4;
        private const int Lava = 5;
        private const int CategoryCount = 6;
        private const int Radius = 18;
        private const int MinBodyTiles = 10;
        private const int ThrottleFrames = 6;
        private const int RescanTicks = 250;
        private const float InvRadius = 1f / Radius;
        private const float SmoothSpeed = 10f;
        private const float ZoomFull = 0.25f;
        private const float ZoomMute = 0.8f;
        private const float MusicDuckFloor = 0.3f;
        private const float MusicAudibleFloor = 0.001f;

        private static readonly string[] SoundByCategory = new string[] { null, "WaterSFX_Coast", "WaterSFX_River", "WaterSFX_Still", "WaterSFX_Marsh", "WaterSFX_Lava_Still" };

        private static byte[] categoryByTerrain;
        private static byte[] overrideByTerrain;

        private byte[] staticType;
        private byte[] waterType;
        private float[] lastProximity;
        private int[] bestDist;
        private float[] targetVol;
        private float[] curVol;
        private SoundDef[] sounds;
        private Sustainer[] sustainers;
        private CameraDriver camera;
        private MusicManagerPlay musicManager;
        private int mapWidth;
        private int mapHeight;
        private float zoomMin;
        private float zoomSpan;
        private bool zoomReady;
        private int lastUpdateFrame;
        private int lastX;
        private int lastZ;
        private bool haveLast;
        private bool scanDirty;
        private int nextRescanTick;
        private bool subscribed;
        private bool built;
        private bool ready;

        public MapComponent_WaterSFX(Map map) : base(map)
        {
        }

        public override void FinalizeInit()
        {
            LongEventHandler.ExecuteWhenFinished(new Action(this.Build));
        }

        public override void MapRemoved()
        {
            if (this.subscribed)
            {
                this.map.events.TerrainChanged -= this.OnTerrainChanged;
                this.subscribed = false;
            }
            this.EndSustainers();
        }

        public override void MapComponentUpdate()
        {
            if (!this.ready)
            {
                return;
            }
            if (this.map != Find.CurrentMap)
            {
                this.EndSustainers();
                this.haveLast = false;
                return;
            }

            CameraDriver cam = this.camera;
            if (cam == null)
            {
                cam = Find.CameraDriver;
                if (cam == null)
                {
                    return;
                }
                this.camera = cam;
            }

            if (!this.zoomReady)
            {
                FloatRange range = cam.config.sizeRange;
                this.zoomMin = range.min;
                this.zoomSpan = range.max - range.min;
                this.zoomReady = true;
            }

            if (Find.TickManager.Paused)
            {
                return;
            }

            int frame = Time.frameCount;
            if (frame - this.lastUpdateFrame >= ThrottleFrames)
            {
                this.lastUpdateFrame = frame;
                this.UpdateTargets(cam);
            }

            this.ApplyVolumes();
        }

        private void UpdateTargets(CameraDriver cam)
        {
            IntVec3 cell = cam.MapPosition;
            if (cell.x < 0 || cell.x >= this.mapWidth || cell.z < 0 || cell.z >= this.mapHeight)
            {
                for (int c = 1; c < CategoryCount; c++)
                {
                    this.targetVol[c] = 0f;
                }
                this.haveLast = false;
                return;
            }

            int tick = Find.TickManager.TicksGame;
            if (!this.haveLast || cell.x != this.lastX || cell.z != this.lastZ || this.scanDirty || tick >= this.nextRescanTick)
            {
                this.Scan(cell.x, cell.z);
                this.lastX = cell.x;
                this.lastZ = cell.z;
                this.haveLast = true;
                this.scanDirty = false;
                this.nextRescanTick = tick + RescanTicks;
            }

            float zoom = this.ZoomFactor(cam.RootSize);
            float waterStep = zoom * this.MusicFactor(false) * InvRadius;
            float lavaStep = zoom * this.MusicFactor(true) * InvRadius;
            for (int c = 1; c < CategoryCount; c++)
            {
                if (this.sounds[c] == null)
                {
                    continue;
                }
                float step = (c == Lava) ? lavaStep : waterStep;
                this.targetVol[c] = this.lastProximity[c] * step;
            }
        }

        private void Scan(int camX, int camZ)
        {
            int[] best = this.bestDist;
            for (int c = 1; c < CategoryCount; c++)
            {
                best[c] = Radius;
            }

            int w = this.mapWidth;
            int minX = camX - Radius;
            if (minX < 0)
            {
                minX = 0;
            }
            int maxX = camX + Radius;
            if (maxX > w - 1)
            {
                maxX = w - 1;
            }
            int minZ = camZ - Radius;
            if (minZ < 0)
            {
                minZ = 0;
            }
            int maxZ = camZ + Radius;
            if (maxZ > this.mapHeight - 1)
            {
                maxZ = this.mapHeight - 1;
            }

            byte[] types = this.waterType;
            byte[] table = categoryByTerrain;
            TerrainGrid grid = this.map.terrainGrid;

            for (int z = minZ; z <= maxZ; z++)
            {
                int dz = z - camZ;
                if (dz < 0)
                {
                    dz = -dz;
                }
                int row = z * w;
                for (int x = minX; x <= maxX; x++)
                {
                    int i = row + x;
                    byte cat = types[i];
                    if (cat == 0)
                    {
                        continue;
                    }
                    int dx = x - camX;
                    if (dx < 0)
                    {
                        dx = -dx;
                    }
                    int d = (dx > dz) ? dx : dz;
                    if (d >= best[cat])
                    {
                        continue;
                    }
                    if (table[grid.TerrainAt(i).index] != 0)
                    {
                        best[cat] = d;
                    }
                }
            }

            for (int c = 1; c < CategoryCount; c++)
            {
                this.lastProximity[c] = (float)(Radius - best[c]);
            }
        }

        private float MusicFactor(bool lava)
        {
            MusicManagerPlay music = this.musicManager;
            if (music == null)
            {
                music = Find.MusicManagerPlay;
                if (music == null)
                {
                    return 1f;
                }
                this.musicManager = music;
            }
            if (!(music.IsPlaying && music.CurSanitizedVolume > MusicAudibleFloor))
            {
                return 1f;
            }
            WaterSFXSettings settings = WaterSFXMod.Settings;
            if (settings == null)
            {
                return MusicDuckFloor;
            }
            bool disable = lava ? settings.disableDuckingLava : settings.disableDucking;
            if (disable)
            {
                return 1f;
            }
            bool silence = lava ? settings.silenceUnderMusicLava : settings.silenceUnderMusic;
            return silence ? 0f : MusicDuckFloor;
        }

        private void ApplyVolumes()
        {
            float t = SmoothSpeed * Time.deltaTime;
            if (t > 1f)
            {
                t = 1f;
            }
            for (int c = 1; c < CategoryCount; c++)
            {
                if (this.sounds[c] == null)
                {
                    continue;
                }
                float cur = this.curVol[c];
                float target = this.targetVol[c];
                float diff = target - cur;
                bool changed;
                if (diff < 0.001f && diff > -0.001f)
                {
                    changed = cur != target;
                    cur = target;
                }
                else
                {
                    cur += diff * t;
                    changed = true;
                }
                this.curVol[c] = cur;
                Sustainer sustainer = this.sustainers[c];
                if (sustainer == null || sustainer.Ended)
                {
                    if (cur <= 0f && target <= 0f)
                    {
                        continue;
                    }
                    sustainer = this.sounds[c].TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.None));
                    this.sustainers[c] = sustainer;
                    changed = true;
                }
                if (sustainer != null && changed)
                {
                    sustainer.externalParams["Vol"] = cur;
                }
            }
        }

        private void EndSustainers()
        {
            Sustainer[] arr = this.sustainers;
            if (arr == null)
            {
                return;
            }
            for (int c = 1; c < CategoryCount; c++)
            {
                Sustainer sustainer = arr[c];
                if (sustainer != null)
                {
                    sustainer.End();
                    arr[c] = null;
                    this.curVol[c] = 0f;
                }
            }
        }

        private float ZoomFactor(float zoom)
        {
            float t = (zoom - this.zoomMin) / this.zoomSpan;
            if (t <= ZoomFull)
            {
                return 1f;
            }
            if (t >= ZoomMute)
            {
                return 0f;
            }
            return 1f - (t - ZoomFull) / (ZoomMute - ZoomFull);
        }

        private void Build()
        {
            if (this.built)
            {
                return;
            }
            this.built = true;

            byte[] table = CategoryTable();
            byte[] overrides = overrideByTerrain;
            IntVec3 size = this.map.Size;
            this.mapWidth = size.x;
            this.mapHeight = size.z;
            int cellCount = size.x * size.z;
            TerrainGrid grid = this.map.terrainGrid;

            byte[] staticType = new byte[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                staticType[i] = table[grid.TerrainAtIgnoreTemp(i).index];
            }

            bool[] visited = new bool[cellCount];
            SuppressSmallBodies(staticType, visited, (byte)Still, size.x, size.z, MinBodyTiles);
            SuppressSmallBodies(staticType, visited, (byte)Marsh, size.x, size.z, MinBodyTiles);

            byte[] waterType = new byte[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                byte ov = overrides[grid.TerrainAt(i).index];
                waterType[i] = (ov != 0) ? ov : staticType[i];
            }

            this.staticType = staticType;
            this.waterType = waterType;
            this.lastProximity = new float[CategoryCount];
            this.bestDist = new int[CategoryCount];
            this.targetVol = new float[CategoryCount];
            this.curVol = new float[CategoryCount];
            this.sounds = new SoundDef[CategoryCount];
            this.sustainers = new Sustainer[CategoryCount];

            for (int c = 1; c < CategoryCount; c++)
            {
                this.sounds[c] = DefDatabase<SoundDef>.GetNamed(SoundByCategory[c], true);
            }

            this.ready = true;
            this.map.events.TerrainChanged += this.OnTerrainChanged;
            this.subscribed = true;
        }

        private void OnTerrainChanged(IntVec3 cell)
        {
            if (!this.ready)
            {
                return;
            }
            int i = cell.z * this.mapWidth + cell.x;
            byte ov = overrideByTerrain[this.map.terrainGrid.TerrainAt(i).index];
            byte cat = (ov != 0) ? ov : this.staticType[i];
            byte old = this.waterType[i];
            if (cat != old)
            {
                this.waterType[i] = cat;
            }
            if (cat == 0 && old == 0)
            {
                return;
            }
            if (!this.haveLast)
            {
                this.scanDirty = true;
                return;
            }
            int dx = cell.x - this.lastX;
            if (dx < 0)
            {
                dx = -dx;
            }
            int dz = cell.z - this.lastZ;
            if (dz < 0)
            {
                dz = -dz;
            }
            if (dx <= Radius && dz <= Radius)
            {
                this.scanDirty = true;
            }
        }

        private static void SuppressSmallBodies(byte[] waterType, bool[] visited, byte target, int width, int height, int minSize)
        {
            int cellCount = width * height;
            Queue<int> queue = new Queue<int>();
            List<int> component = new List<int>();
            for (int start = 0; start < cellCount; start++)
            {
                if (waterType[start] != target || visited[start])
                {
                    continue;
                }
                component.Clear();
                queue.Enqueue(start);
                visited[start] = true;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    component.Add(i);
                    int x = i % width;
                    int z = i / width;
                    bool left = x > 0;
                    bool right = x < width - 1;
                    bool down = z > 0;
                    bool up = z < height - 1;
                    if (left)
                    {
                        Visit(waterType, visited, queue, i - 1, target);
                    }
                    if (right)
                    {
                        Visit(waterType, visited, queue, i + 1, target);
                    }
                    if (down)
                    {
                        Visit(waterType, visited, queue, i - width, target);
                    }
                    if (up)
                    {
                        Visit(waterType, visited, queue, i + width, target);
                    }
                    if (left && down)
                    {
                        Visit(waterType, visited, queue, i - width - 1, target);
                    }
                    if (left && up)
                    {
                        Visit(waterType, visited, queue, i + width - 1, target);
                    }
                    if (right && down)
                    {
                        Visit(waterType, visited, queue, i - width + 1, target);
                    }
                    if (right && up)
                    {
                        Visit(waterType, visited, queue, i + width + 1, target);
                    }
                }
                if (component.Count < minSize)
                {
                    for (int k = 0; k < component.Count; k++)
                    {
                        waterType[component[k]] = 0;
                    }
                }
            }
        }

        private static void Visit(byte[] waterType, bool[] visited, Queue<int> queue, int n, byte target)
        {
            if (!visited[n] && waterType[n] == target)
            {
                visited[n] = true;
                queue.Enqueue(n);
            }
        }

        private static byte[] CategoryTable()
        {
            if (categoryByTerrain != null)
            {
                return categoryByTerrain;
            }

            byte[] table = new byte[65536];
            byte[] overrides = new byte[65536];
            List<TerrainDef> defs = DefDatabase<TerrainDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                TerrainDef def = defs[i];
                byte value;
                if (def.IsFlood)
                {
                    value = River;
                    overrides[def.index] = River;
                }
                else if (HasToken(def.defName, "lava") && !HasToken(def.defName, "cooled"))
                {
                    value = Lava;
                    overrides[def.index] = Lava;
                }
                else if (HasToken(def.defName, "marsh"))
                {
                    value = Marsh;
                }
                else if (def.IsOcean)
                {
                    value = Coast;
                }
                else if (def.IsRiver)
                {
                    value = River;
                }
                else if (def.IsWater)
                {
                    value = Still;
                }
                else
                {
                    value = 0;
                }
                table[def.index] = value;
            }

            overrideByTerrain = overrides;
            categoryByTerrain = table;
            return table;
        }

        private static bool HasToken(string text, string token)
        {
            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
