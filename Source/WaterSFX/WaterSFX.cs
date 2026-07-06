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

        public override void ExposeData()
        {
            Scribe_Values.Look<bool>(ref this.silenceUnderMusic, "silenceUnderMusic", false, false);
            Scribe_Values.Look<bool>(ref this.disableDucking, "disableDucking", false, false);
        }
    }

    public class WaterSFXMod : Mod
    {
        public static WaterSFXSettings Settings;

        public WaterSFXMod(ModContentPack content) : base(content)
        {
            Settings = this.GetSettings<WaterSFXSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            bool wasSilence = Settings.silenceUnderMusic;
            listing.CheckboxLabeled("Silence Water While Music Plays", ref Settings.silenceUnderMusic, "Silences the water when music is playing, like other ambience in the game.", 0f, 1f);
            if (Settings.silenceUnderMusic && !wasSilence)
            {
                Settings.disableDucking = false;
            }
            bool wasDisable = Settings.disableDucking;
            listing.CheckboxLabeled("Disable Music Ducking", ref Settings.disableDucking, "Keeps the water at full volume even while music is playing.", 0f, 1f);
            if (Settings.disableDucking && !wasDisable)
            {
                Settings.silenceUnderMusic = false;
            }
            listing.End();
        }

        public override string SettingsCategory()
        {
            return "WaterSFX";
        }
    }

    public class MapComponent_WaterSFX : MapComponent
    {
        private const int Coast = 1;
        private const int River = 2;
        private const int Still = 3;
        private const int Marsh = 4;
        private const int CategoryCount = 5;
        private const int Radius = 24;
        private const int MinBodyTiles = 10;
        private const float InvRadius = 1f / Radius;
        private const float ZoomFull = 0.25f;
        private const float ZoomMute = 0.8f;
        private const float MusicDuckFloor = 0.3f;
        private const float MusicRampSpeed = 0.8f;
        private const float MusicAudibleFloor = 0.001f;

        private static readonly string[] SoundByCategory = new string[] { null, "WaterSFX_Coast", "WaterSFX_River", "WaterSFX_Still", "WaterSFX_Marsh" };

        private static byte[] categoryByTerrain;

        private byte[][] fields;
        private byte[] lastProximity;
        private SoundDef[] sounds;
        private Sustainer[] sustainers;
        private CameraDriver camera;
        private MusicManagerPlay musicManager;
        private float musicRamp;
        private int activeCount;
        private int mapWidth;
        private int mapHeight;
        private float zoomMin;
        private float zoomSpan;
        private bool zoomReady;
        private int lastX;
        private int lastZ;
        private float lastGain;
        private bool haveLast;
        private bool built;
        private bool ready;

        public MapComponent_WaterSFX(Map map) : base(map)
        {
        }

        public override void FinalizeInit()
        {
            LongEventHandler.ExecuteWhenFinished(new Action(this.Build));
        }

        public override void MapComponentUpdate()
        {
            if (!this.ready)
            {
                return;
            }
            if (this.map != Find.CurrentMap)
            {
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
                this.MaintainActive();
                return;
            }

            IntVec3 cell = cam.MapPosition;
            float music = this.MusicFactor();
            float gain = this.ZoomFactor(cam.RootSize) * music;

            if (this.haveLast && cell.x == this.lastX && cell.z == this.lastZ && gain == this.lastGain)
            {
                this.MaintainActive();
                return;
            }

            if (cell.x < 0 || cell.x >= this.mapWidth || cell.z < 0 || cell.z >= this.mapHeight)
            {
                this.MaintainActive();
                return;
            }

            int index = cell.z * this.mapWidth + cell.x;

            if (this.haveLast && gain == this.lastGain && this.ProximityUnchanged(index))
            {
                this.lastX = cell.x;
                this.lastZ = cell.z;
                this.MaintainActive();
                return;
            }

            float step = gain * InvRadius;
            for (int a = 0; a < this.activeCount; a++)
            {
                byte proximity = this.fields[a][index];
                this.lastProximity[a] = proximity;
                Sustainer sustainer = this.sustainers[a];
                if (sustainer == null || sustainer.Ended)
                {
                    sustainer = this.sounds[a].TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.PerFrame));
                    this.sustainers[a] = sustainer;
                }
                if (sustainer != null)
                {
                    sustainer.Maintain();
                    sustainer.externalParams["Vol"] = proximity * step;
                }
            }
            this.lastX = cell.x;
            this.lastZ = cell.z;
            this.lastGain = gain;
            this.haveLast = true;
        }

        private bool ProximityUnchanged(int index)
        {
            for (int a = 0; a < this.activeCount; a++)
            {
                if (this.fields[a][index] != this.lastProximity[a])
                {
                    return false;
                }
            }
            return true;
        }

        private float MusicFactor()
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
            float target = (music.IsPlaying && music.CurSanitizedVolume > MusicAudibleFloor) ? 1f : 0f;
            float delta = MusicRampSpeed * Time.deltaTime;
            if (this.musicRamp < target)
            {
                this.musicRamp += delta;
                if (this.musicRamp > target)
                {
                    this.musicRamp = target;
                }
            }
            else if (this.musicRamp > target)
            {
                this.musicRamp -= delta;
                if (this.musicRamp < target)
                {
                    this.musicRamp = target;
                }
            }
            if (this.musicRamp <= 0f)
            {
                return 1f;
            }
            WaterSFXSettings settings = WaterSFXMod.Settings;
            if (settings != null && settings.disableDucking)
            {
                return 1f;
            }
            float floor = (settings != null && settings.silenceUnderMusic) ? 0f : MusicDuckFloor;
            return 1f - this.musicRamp * (1f - floor);
        }

        private void MaintainActive()
        {
            for (int a = 0; a < this.activeCount; a++)
            {
                Sustainer sustainer = this.sustainers[a];
                if (sustainer != null)
                {
                    if (sustainer.Ended)
                    {
                        this.sustainers[a] = null;
                    }
                    else
                    {
                        sustainer.Maintain();
                    }
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
            IntVec3 size = this.map.Size;
            this.mapWidth = size.x;
            this.mapHeight = size.z;
            int cellCount = size.x * size.z;
            TerrainGrid grid = this.map.terrainGrid;

            byte[] waterType = new byte[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                waterType[i] = table[grid.TerrainAt(i).index];
            }

            bool[] visited = new bool[cellCount];
            SuppressSmallBodies(waterType, visited, (byte)Still, size.x, size.z, MinBodyTiles);
            SuppressSmallBodies(waterType, visited, (byte)Marsh, size.x, size.z, MinBodyTiles);

            byte[][] tempFields = new byte[CategoryCount][];
            Queue<int>[] frontiers = new Queue<int>[CategoryCount];
            bool[] present = new bool[CategoryCount];
            for (int c = 1; c < CategoryCount; c++)
            {
                tempFields[c] = new byte[cellCount];
                frontiers[c] = new Queue<int>();
            }

            for (int i = 0; i < cellCount; i++)
            {
                int cat = waterType[i];
                if (cat != 0)
                {
                    tempFields[cat][i] = Radius;
                    frontiers[cat].Enqueue(i);
                    present[cat] = true;
                }
            }

            int active = 0;
            for (int c = 1; c < CategoryCount; c++)
            {
                if (present[c])
                {
                    active++;
                }
            }

            this.fields = new byte[active][];
            this.lastProximity = new byte[active];
            this.sounds = new SoundDef[active];
            this.sustainers = new Sustainer[active];

            int a = 0;
            for (int c = 1; c < CategoryCount; c++)
            {
                if (!present[c])
                {
                    continue;
                }
                Fill(tempFields[c], frontiers[c], size.x, size.z);
                this.fields[a] = tempFields[c];
                this.sounds[a] = DefDatabase<SoundDef>.GetNamed(SoundByCategory[c], true);
                a++;
            }

            this.activeCount = active;
            this.ready = active > 0;
        }

        private static void Fill(byte[] field, Queue<int> frontier, int width, int height)
        {
            while (frontier.Count > 0)
            {
                int i = frontier.Dequeue();
                byte cur = field[i];
                if (cur <= 1)
                {
                    continue;
                }
                byte next = (byte)(cur - 1);
                int x = i % width;
                int z = i / width;
                bool left = x > 0;
                bool right = x < width - 1;
                bool down = z > 0;
                bool up = z < height - 1;
                if (left)
                {
                    Step(field, frontier, i - 1, next);
                }
                if (right)
                {
                    Step(field, frontier, i + 1, next);
                }
                if (down)
                {
                    Step(field, frontier, i - width, next);
                }
                if (up)
                {
                    Step(field, frontier, i + width, next);
                }
                if (left && down)
                {
                    Step(field, frontier, i - width - 1, next);
                }
                if (left && up)
                {
                    Step(field, frontier, i + width - 1, next);
                }
                if (right && down)
                {
                    Step(field, frontier, i - width + 1, next);
                }
                if (right && up)
                {
                    Step(field, frontier, i + width + 1, next);
                }
            }
        }

        private static void Step(byte[] field, Queue<int> frontier, int n, byte value)
        {
            if (field[n] < value)
            {
                field[n] = value;
                frontier.Enqueue(n);
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
            List<TerrainDef> defs = DefDatabase<TerrainDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                TerrainDef def = defs[i];
                byte value;
                if (HasToken(def.defName, "marsh"))
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

            categoryByTerrain = table;
            return table;
        }

        private static bool HasToken(string text, string token)
        {
            return text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
