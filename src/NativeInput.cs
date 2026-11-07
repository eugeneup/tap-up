using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TapUp
{
    internal interface IInput
    {
        bool ShouldPause();
        void Send(bool keyUp);
    }

    internal sealed class NativeInput : IInput
    {
        internal const ushort Key = 0x7c; // F13, also present in Bongo Cat's WinKeys enum.
        internal static readonly UIntPtr Marker = new UIntPtr(0x54415055);
        internal static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));
        private static readonly int[] Modifiers = { 0x10, 0x11, 0x12, 0x5b, 0x5c };
        private readonly INPUT[] down;
        private readonly INPUT[] up;

        internal NativeInput()
        {
            INPUT input = new INPUT();
            input.type = 1;
            input.data.keyboard.wVk = Key;
            input.data.keyboard.wScan = (ushort)MapVirtualKey(Key, 0);
            input.data.keyboard.dwExtraInfo = Marker;
            down = new[] { input };
            input.data.keyboard.dwFlags = 2;
            up = new[] { input };
        }

        public bool ShouldPause()
        {
            // Never turn a user's modifier into an unintended F13 shortcut.
            foreach (int key in Modifiers)
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }

        public void Send(bool keyUp)
        {
            if (SendInput(1, keyUp ? up : down, InputSize) != 1)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    "Windows не приняла ввод (код " + error + "). Проверьте, что BongoCat и Tap-Up запущены с одинаковыми правами.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT { public uint type; public InputUnion data; }
        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT keyboard;
            [FieldOffset(0)] public MOUSEINPUT mouse;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        // The entire INPUT union is required, including its largest member.
        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint type);
    }
}
