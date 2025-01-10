using UnityEngine;
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SS
{
    public enum Role
    {
        System = 1,
        Assistant,
        User,
        [Obsolete("Use Tool")]
        Function,
        Tool
    }
    
    [System.Serializable]
    public class FineTuneMessage
    {
        public string role;
        public string content;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        [Range(0, 1)]
        public float? weight;
    }
    
    [System.Serializable]
    public class FineTuneDataSet
    {
        public List<FineTuneMessage> messages = null;
    }
}