using System;
using UnityEngine;

namespace KMusic.Core
{
    [Serializable]
    public sealed class Parameter
    {
        public string Id;
        public float Min;
        public float Max;
        public float Default;
        public bool LogScale;
        public string Unit;

        [NonSerialized] private float _value;

        public float Value
        {
            get => _value;
            set => _value = Mathf.Clamp(value, Min, Max);
        }

        public Parameter(string id, float min, float max, float def, bool log=false, string unit="")
        {
            Id = id; Min = min; Max = max; Default = def; LogScale = log; Unit = unit;
            _value = def;
        }

        public float Normalized
        {
            get
            {
                if (Mathf.Approximately(Max, Min)) return 0f;
                if (!LogScale) return Mathf.InverseLerp(Min, Max, Value);
                // log mapping (Min must be > 0)
                float mn = Mathf.Max(0.0001f, Min);
                float mx = Mathf.Max(mn + 0.0001f, Max);
                return Mathf.InverseLerp(Mathf.Log(mn), Mathf.Log(mx), Mathf.Log(Mathf.Max(mn, Value)));
            }
            set
            {
                float t = Mathf.Clamp01(value);
                if (!LogScale) Value = Mathf.Lerp(Min, Max, t);
                else
                {
                    float mn = Mathf.Max(0.0001f, Min);
                    float mx = Mathf.Max(mn + 0.0001f, Max);
                    float v = Mathf.Exp(Mathf.Lerp(Mathf.Log(mn), Mathf.Log(mx), t));
                    Value = v;
                }
            }
        }

        public string Format()
        {
            if (Unit == "bpm") return $"{Mathf.RoundToInt(Value)} BPM";
            if (Unit == "ms") return $"{Mathf.RoundToInt(Value)} ms";
            if (Unit == "%") return $"{Mathf.RoundToInt(Value)}%";
            if (Unit == "hz")
            {
                if (Value >= 1000f) return $"{(Value/1000f):0.0} kHz";
                return $"{Mathf.RoundToInt(Value)} Hz";
            }
            return $"{Value:0.##}{(string.IsNullOrEmpty(Unit) ? "" : " " + Unit)}";
        }
    }
}
