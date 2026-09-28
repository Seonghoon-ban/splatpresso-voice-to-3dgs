using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace SplatPresso
{
    /// <summary>
    /// Records an estimated cost (GenPresso credits) per external API call and enforces a hard per-run cap.
    /// </summary>
    /// <remarks>
    /// Estimates only need to be the right order of magnitude - the point is stopping runaway retry loops,
    /// not accounting. The file (ledger.json) is append-only history; every run (a fresh request or a replay
    /// of the same session) starts a new section with <see cref="BeginRun"/>, and <see cref="TotalCost"/> /
    /// <see cref="CanSpend"/> only count the current section, so a replay does not inherit the earlier spend.
    /// </remarks>
    [Serializable]
    public sealed class CostLedger
    {
        /// <summary>One recorded call.</summary>
        [Serializable]
        public class Entry
        {
            public string item;
            public double cost;
            public string timestamp; // local time, HH:mm:ss
            public int run;          // run section the entry belongs to
        }

        public List<Entry> entries = new List<Entry>();
        /// <summary>Cap (estimated credits) for the current run section.</summary>
        public double capCost = 25.0;
        /// <summary>Index of the current run section (0 for the first run).</summary>
        public int currentRun;

        [NonSerialized] string m_SavePath;

        /// <summary>Estimated credits spent in the current run section.</summary>
        [JsonIgnore]
        public double TotalCost
        {
            get
            {
                double total = 0;
                foreach (var e in entries)
                    if (e != null && e.run == currentRun) total += e.cost;
                return total;
            }
        }

        /// <summary>Estimated credits spent across every run recorded in this ledger.</summary>
        [JsonIgnore]
        public double LifetimeCost
        {
            get
            {
                double total = 0;
                foreach (var e in entries)
                    if (e != null) total += e.cost;
                return total;
            }
        }

        /// <summary>Loads the ledger at <paramref name="path"/> (tolerates a missing or corrupt file).</summary>
        public static CostLedger LoadOrCreate(string path)
        {
            CostLedger ledger = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { ledger = JsonUtil.Deserialize<CostLedger>(File.ReadAllText(path)); }
                catch (Exception e) { Debug.LogWarning($"[SplatPresso] Failed to load ledger {path}: {e.Message}"); }
            }
            ledger ??= new CostLedger();
            ledger.entries ??= new List<Entry>();
            ledger.m_SavePath = path;
            return ledger;
        }

        /// <summary>In-memory ledger (nothing is written to disk).</summary>
        public static CostLedger InMemory(double cap) => new CostLedger { capCost = cap };

        /// <summary>
        /// Starts a new run section with the given cap. The first run of a fresh ledger stays section 0.
        /// Nothing is written until the next <see cref="Record"/>.
        /// </summary>
        public void BeginRun(double cap)
        {
            capCost = cap;
            int maxRun = -1;
            foreach (var e in entries)
                if (e != null && e.run > maxRun) maxRun = e.run;
            currentRun = maxRun + 1;
        }

        /// <summary>
        /// True when <paramref name="cost"/> fits under the cap. Logs and returns false otherwise; callers must
        /// not make the call in that case.
        /// </summary>
        public bool CanSpend(double cost)
        {
            double total = TotalCost;
            if (total + cost <= capCost)
                return true;
            Debug.LogWarning($"[SplatPresso] Cost cap reached: {total:F2} + {cost:F2} > {capCost:F2} credits (estimated)");
            return false;
        }

        /// <summary>Appends an entry to the current run section and saves immediately.</summary>
        public void Record(string item, double cost)
        {
            entries.Add(new Entry
            {
                item = item,
                cost = cost,
                timestamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                run = currentRun,
            });
            Save();
        }

        void Save()
        {
            if (string.IsNullOrEmpty(m_SavePath))
                return;
            try { File.WriteAllText(m_SavePath, JsonUtil.Serialize(this)); }
            catch (Exception e) { Debug.LogWarning($"[SplatPresso] Failed to save ledger: {e.Message}"); }
        }
    }
}
