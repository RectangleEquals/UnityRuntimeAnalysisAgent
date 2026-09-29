// Types for the survey: Unity's field-serialization rules, Unity messages, singleton-looking statics and a custom
// serializer marker.
using System;
using System.Collections.Generic;
using UnityEngine;
using Zoo.Il;

namespace Zoo.Survey
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field)]
    public sealed class FixtureSerializedAttribute : Attribute
    {
        public FixtureSerializedAttribute(string format = "binary") => Format = format;

        public string Format { get; }

        public int Version { get; set; }
    }

    [Serializable]
    public class Stats
    {
        public int hp = 10;
        [SerializeField] private float speed = 1;
        private int hidden;
        public Dictionary<string, int> dict = new();
    }

    public class Enemy : MonoBehaviour
    {
        public static int count;
        public readonly int readOnly = 1;
        public int health = 5;
        [SerializeField] private string id = "e1";
        [NonSerialized] public int temporary;
        public List<Stats> stats = new();
        public Stats[] statsArray = Array.Empty<Stats>();
        public Dictionary<string, int> map = new();
        [SerializeReference] public IShape shape;
        public Box<int> generic = new();
        public List<List<int>> nested = new();
        private int secret;

        private void Awake()
        {
        }

        private void Update()
        {
        }

        private void OnTriggerEnter2D(object other)
        {
        }

        public void NotAMessage()
        {
        }
    }

    public class ItemDatabase : ScriptableObject
    {
        public List<string> items = new();

        private void OnEnable()
        {
        }
    }

    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance;
    }

    public abstract class Singleton<T>
    {
        public static T Current { get; set; }
    }

    public sealed class AudioManager : Singleton<AudioManager>
    {
    }

    public static class Services
    {
        public static object Instance;
        public const int Limit = 3;
    }

    [FixtureSerialized("json", Version = 2)]
    public class Custom : MonoBehaviour
    {
        [FixtureSerialized] private Dictionary<string, int> table = new();
        public int plain;
    }
}
