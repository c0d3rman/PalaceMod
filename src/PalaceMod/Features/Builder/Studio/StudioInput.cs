using System.Collections.Generic;
using UnityEngine;

/*
    The pointer and modifier keys as the Studio reads them: Unity's Input (which SimulatedPointer also drives), or a
    scripted gesture from the dev command (`builder-studio drag ...`), so drags, marquees and right-drag pans can be
    tested without a human. A script is a list of frames, each a screen point and the buttons held.
*/
namespace PalaceMod.Builder.Studio
{
    internal static class StudioInput
    {
        private struct Frame
        {
            public Vector2 Pos;
            public int Buttons;   // bit 0 left, 1 right, 2 middle
            public bool Shift, Ctrl, Alt;
        }

        private static readonly Queue<Frame> script = new();
        private static Frame current, previous;
        private static bool scripted;
        private static int frame = -1;

        public static bool Scripted => scripted;

        /// <summary>Queues a drag from a to b with a button held (0 left, 1 right, 2 middle), over a number of frames.</summary>
        public static void Drag(Vector2 a, Vector2 b, int button, int frames, bool shift = false, bool ctrl = false, bool alt = false)
        {
            int bit = 1 << button;
            script.Enqueue(new Frame { Pos = a, Buttons = 0, Shift = shift, Ctrl = ctrl, Alt = alt });
            for (int i = 0; i <= frames; i++)
                script.Enqueue(new Frame { Pos = Vector2.Lerp(a, b, frames == 0 ? 1f : (float)i / frames), Buttons = bit, Shift = shift, Ctrl = ctrl, Alt = alt });
            script.Enqueue(new Frame { Pos = b, Buttons = 0, Shift = shift, Ctrl = ctrl, Alt = alt });
            script.Enqueue(new Frame { Pos = b, Buttons = 0 });
        }

        /// <summary>Queues a click (press and release in place).</summary>
        public static void Click(Vector2 at, int button, bool shift = false, bool ctrl = false, bool alt = false) => Drag(at, at, button, 1, shift, ctrl, alt);

        /// <summary>Queues a pointer move with nothing held.</summary>
        public static void MoveTo(Vector2 at)
        {
            script.Enqueue(new Frame { Pos = at });
            script.Enqueue(new Frame { Pos = at });
        }

        /// <summary>Once per frame, before anything reads the pointer.</summary>
        public static void Tick()
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            previous = current;
            if (script.Count > 0)
            {
                scripted = true;
                current = script.Dequeue();
                return;
            }
            if (scripted && previous.Buttons != 0) { current = previous; current.Buttons = 0; return; }
            scripted = false;
            current = new Frame
            {
                Pos = Input.mousePosition,
                Buttons = (Input.GetMouseButton(0) ? 1 : 0) | (Input.GetMouseButton(1) ? 2 : 0) | (Input.GetMouseButton(2) ? 4 : 0),
                Shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
                Ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand),
                Alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            };
            // A press and release within one frame (a fast click) still counts as a press.
            if (Input.GetMouseButtonDown(0) && (current.Buttons & 1) == 0) current.Buttons |= 1;
        }

        public static Vector2 Mouse => current.Pos;
        public static bool Held(int b) => (current.Buttons & (1 << b)) != 0;
        public static bool Down(int b) => Held(b) && (previous.Buttons & (1 << b)) == 0;
        public static bool Up(int b) => !Held(b) && (previous.Buttons & (1 << b)) != 0;
        public static bool Shift => current.Shift;
        /// <summary>Ctrl or Cmd.</summary>
        public static bool Ctrl => current.Ctrl;
        public static bool Alt => current.Alt;

        public static void Reset()
        {
            script.Clear();
            scripted = false;
            current = previous = default;
        }
    }
}
