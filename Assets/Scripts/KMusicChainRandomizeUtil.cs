using System;
using System.Collections.Generic;
using UnityEngine;
using KMusic.Core;

public static class KMusicChainRandomizeUtil
{
    public static bool TryGetContext(out ChainState chain, out int currentBar, out int currentPatternId)
    {
        PatternBank.EnsureDefaultPatternExists();
        chain = ChainState.LoadOrCreate();
        currentBar = Mathf.Clamp(chain != null ? chain.cursor : 0, 0, 63);
        currentPatternId = -1;

        var chainUi = UnityEngine.Object.FindObjectOfType<KMusicChainUI>();
        if (chainUi != null)
            currentPatternId = chainUi.ResolveSelectedPatternId();

        if (currentPatternId < 0 && chain != null)
            currentPatternId = chain.GetSlot(currentBar);

        if (currentPatternId < 0)
            currentPatternId = 0;

        return chain != null;
    }

    public static int GetBarCount(ChainState chain)
    {
        return Mathf.Clamp(chain != null ? chain.length : 1, 1, 64);
    }

    public static int[] EnsureUniquePatternIdsPerBar(ChainState chain, int currentBar, int currentPatternId, Func<PatternData> livePatternFactory)
    {
        if (chain == null)
            return Array.Empty<int>();

        int len = GetBarCount(chain);
        int[] barPatternIds = new int[len];
        var seen = new HashSet<int>();
        PatternData liveBase = null;

        for (int bar = 0; bar < len; bar++)
        {
            int pid = chain.GetSlot(bar);

            if (bar == currentBar && currentPatternId >= 0)
                pid = currentPatternId;

            if (pid < 0)
            {
                if (liveBase == null)
                    liveBase = SafeClone(livePatternFactory != null ? livePatternFactory() : null);

                pid = PatternBank.CreateFrom(liveBase ?? new PatternData());
            }
            else if (seen.Contains(pid))
            {
                pid = PatternBank.Duplicate(pid);
            }

            seen.Add(pid);
            chain.SetSlot(bar, pid);
            barPatternIds[bar] = pid;
        }

        chain.Save();
        return barPatternIds;
    }

    public static PatternData SafeClone(PatternData data)
    {
        return data != null ? data.Clone() : new PatternData();
    }
}
