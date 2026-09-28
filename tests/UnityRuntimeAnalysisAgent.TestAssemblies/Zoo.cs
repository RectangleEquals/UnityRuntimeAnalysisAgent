// The type zoo: every shape the anchors and the value codec have to handle.
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Zoo
{
    public class Plain
    {
        public static int StaticCount = 3;
        public int number = 42;
        private string text = "hello";
        public Plain next;

        public static string StaticAuto { get; set; } = "static";

        public int Auto { get; set; } = 7;

        public int Computed => number * 2;

        public int Throws => throw new InvalidOperationException("boom");

        public string Text => text;
    }

    public struct Point
    {
        public int X;
        public int Y;

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    public class Outer
    {
        public int outerValue;

        public class Inner
        {
            public int value = 1;

            public class Deeper
            {
                public string where = "deep";
            }
        }
    }

    public class Box<T>
    {
        public T item;
        public List<T> list = new();

        public T Get() => item;

        public TOut Map<TOut>(Func<T, TOut> map) => map(item);
    }

    public class Overloads
    {
        static Overloads()
        {
        }

        public Overloads()
        {
        }

        public Overloads(int x)
        {
        }

        public void Do()
        {
        }

        public void Do(int a)
        {
        }

        public void Do(string s)
        {
        }

        public void Do(int a, string s)
        {
        }
    }

    public class WithEvents
    {
        public event EventHandler Changed;

        public event Action<int> Counted;

        public int Property { get; set; }

        public string this[int index] => index.ToString();
    }

    [Flags]
    public enum Access
    {
        None = 0,
        Read = 1,
        Write = 2,
        Exec = 4,
    }

    public enum Big : ulong
    {
        Max = ulong.MaxValue,
    }

    public class Base
    {
        public int baseField = 1;
        private int shadowed = 10;
    }

    public class Derived : Base
    {
        public int derivedField = 2;
        private int shadowed = 20;
    }

    public class Chain
    {
        public int level;
        public Chain next;

        public static Chain Of(int length)
        {
            Chain head = null;
            for (var i = length - 1; i >= 0; i--)
            {
                head = new Chain { level = i, next = head };
            }

            return head;
        }
    }

    public class Wide
    {
        public int f00, f01, f02, f03, f04, f05, f06, f07, f08, f09;
        public int f10, f11, f12, f13, f14, f15, f16, f17, f18, f19;
        public int f20, f21, f22, f23, f24, f25, f26, f27, f28, f29;
        public int f30, f31, f32, f33, f34, f35, f36, f37, f38, f39;
        public int f40, f41, f42, f43, f44, f45, f46, f47, f48, f49;
        public int f50, f51, f52, f53, f54, f55, f56, f57, f58, f59;
        public int f60, f61, f62, f63, f64, f65, f66, f67, f68, f69;
    }

    public class Player : MonoBehaviour
    {
        public int health = 100;
        public Vector3 position = new(1.5f, 0, -2);
        public Plain stats = new();
    }

    public class Settings : ScriptableObject
    {
        public float volume = 0.5f;
    }

    /// <summary>One of every kind of value.</summary>
    public class Everything
    {
        public bool flag = true;
        public sbyte tiny = -8;
        public byte octet = 200;
        public short small = -300;
        public ushort usmall = 60000;
        public int whole = -123456;
        public uint uwhole = 4000000000;
        public long safeLong = 9007199254740992;
        public long bigLong = -9007199254740993;
        public ulong bigULong = ulong.MaxValue;
        public float single = 0.1f;
        public float notANumber = float.NaN;
        public double infinity = double.PositiveInfinity;
        public double real = 2.5;
        public decimal money = 12.34m;
        public char letter = 'Q';
        public string text = "hello";
        public string nothing;
        public string longText = new('x', 40);
        public Guid id = new("5b0e1c7a-3f2d-4e8b-9a61-0c2d4e6f8a1b");
        public DateTime when = new(2026, 9, 28, 12, 30, 0, DateTimeKind.Utc);
        public DateTimeOffset whenOffset = new(2026, 9, 28, 12, 30, 0, TimeSpan.FromHours(2));
        public TimeSpan span = new(1, 2, 3, 4);
        public Access access = Access.Read | Access.Exec;
        public Big big = Big.Max;
        public Type type = typeof(Plain);
        public Type boxType = typeof(Box<int>);
        public Action callback;
        public int[] array = { 1, 2, 3 };
        public List<string> names = new() { "a", "b", "c", "d", "e" };
        public Dictionary<string, int> scores = new() { ["x"] = 1, ["y"] = 2 };
        public Dictionary<Point, string> byPoint = new() { [new Point(1, 2)] = "p" };
        public HashSet<int> set = new() { 7 };
        public IEnumerable<int> lazy = Numbers();
        public Point point = new(3, 4);
        public Vector3 position = new(1.5f, 0, -2);
        public Vector2Int cell = new() { m_X = 4, m_Y = 5 };
        public Color color = new() { r = 1, g = 0.5f, b = 0, a = 1 };
        public Color32 color32 = new() { r = 255, g = 128, b = 0, a = 255 };
        public Rect rect = new() { m_XMin = 1, m_YMin = 2, m_Width = 3, m_Height = 4 };
        public Bounds bounds = new() { m_Center = new Vector3(1, 2, 3), m_Extents = new Vector3(0.5f, 0.5f, 0.5f) };
        public LayerMask mask = new() { m_Mask = 5 };
        public UnityEvent onUse = new();
        public GameObject owner;
        public Plain plain = new();
        public Plain shared;
        public Everything self;
        public object boxed = 5;

        public Everything()
        {
            shared = plain;
            self = this;
            callback = Callback;
        }

        public void Callback()
        {
        }

        private static IEnumerable<int> Numbers()
        {
            yield return 1;
            yield return 2;
        }
    }
}

namespace Example
{
    /// <summary>The component the protocol's example locators point at (<c>live://Main/Player#Example.Inventory.items</c>).</summary>
    public class Inventory : UnityEngine.MonoBehaviour
    {
        public List<int> items = new() { 1, 2, 3 };
    }
}
