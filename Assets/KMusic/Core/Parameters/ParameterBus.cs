using System;
using System.Collections.Generic;
using UnityEngine;

namespace KMusic.Core
{
    public sealed class ParameterBus
    {
        public event Action<string, float> OnChanged;

        private readonly Dictionary<string, Parameter> _p = new();

        public IEnumerable<Parameter> All => _p.Values;

        public void Add(Parameter p) => _p[p.Id] = p;

        public bool TryGet(string id, out Parameter p) => _p.TryGetValue(id, out p);

        public float GetValue(string id) => _p.TryGetValue(id, out var p) ? p.Value : 0f;

        public float GetNormalized(string id) => _p.TryGetValue(id, out var p) ? p.Normalized : 0f;

        public void SetNormalized(string id, float t)
        {
            if (!_p.TryGetValue(id, out var p)) return;
            float before = p.Value;
            p.Normalized = t;
            if (!Mathf.Approximately(before, p.Value))
                OnChanged?.Invoke(id, p.Value);
        }

        public void SetValue(string id, float v)
        {
            if (!_p.TryGetValue(id, out var p)) return;
            float before = p.Value;
            p.Value = v;
            if (!Mathf.Approximately(before, p.Value))
                OnChanged?.Invoke(id, p.Value);
        }
    }
}
