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
            // Enum-like display names for synth selector params.
            if (!string.IsNullOrEmpty(Id))
            {
                if (Id == "osc1.wave" || Id == "osc2.wave")
                {
                    string[] waveNames =
                    {
                        "SIN", "TRI", "SAW", "SQR", "PWM",
                        "NOI", "S&H", "STEP", "FORM", "HARM", "META"
                    };

                    int index = Mathf.RoundToInt(Normalized * (waveNames.Length - 1));
                    index = Mathf.Clamp(index, 0, waveNames.Length - 1);
                    return waveNames[index];
                }

                if (Id == "fx.dist.type")
                {
                    string[] distNames =
                    {
                        "SOFT", "HARD", "CLIP", "FOLD"
                    };

                    int index = Mathf.RoundToInt(Normalized * (distNames.Length - 1));
                    index = Mathf.Clamp(index, 0, distNames.Length - 1);
                    return distNames[index];
                }
            }

            // Special-case: drum mixer faders are stored as 0..1 in the bus,
            // but we want to *display* mixer-style dB (-80..+6) like a real mixer.
            // This keeps the rest of the app (which expects 0..1) intact.
            if (!string.IsNullOrEmpty(Id) && Id.StartsWith("drum.vol", StringComparison.OrdinalIgnoreCase))
            {
                float t01 = Mathf.Clamp01(Value);

                const float unityAt = 0.80f;
                float db;

                if (t01 <= 0.0001f)
                {
                    return "-\u221E dB";
                }

                if (t01 < unityAt)
                {
                    float u = Mathf.Clamp01(t01 / unityAt);
                    db = Mathf.Lerp(-80f, 0f, u);
                }
                else
                {
                    float u = Mathf.Clamp01((t01 - unityAt) / (1f - unityAt));
                    db = Mathf.Lerp(0f, 6f, u);
                }

                if (db >= 0f) return $"+{db:0.#} dB";
                return $"{db:0} dB";
            }

            if (Unit == "bpm") return $"{Mathf.RoundToInt(Value)} BPM";
            if (Unit == "ms") return $"{Mathf.RoundToInt(Value)} ms";
            if (Unit == "%") return $"{Mathf.RoundToInt(Value)}%";
            if (Unit == "hz")
            {
                if (Value >= 1000f) return $"{(Value / 1000f):0.0} kHz";
                return $"{Mathf.RoundToInt(Value)} Hz";
            }

            return $"{Value:0.##}{(string.IsNullOrEmpty(Unit) ? "" : " " + Unit)}";
        }    
    }
}
