// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using MonoGame.Framework.Utilities;

namespace Microsoft.Xna.Framework
{
    internal class SdlGamePlatform : GamePlatform
    {
        public override GameRunBehavior DefaultRunBehavior
        {
            get { return GameRunBehavior.Synchronous; }
        }

        private readonly Game _game;
        private readonly List<Keys> _keys;

        private int _isExiting;
        private SdlGameWindow _view;

        // If set to true, enables Wayland VSync.
        public bool WaylandVsync { get; set; }

        // The Wayland frame callback handle. When this is non-zero, we are waiting for the compositor to signal us when
        // it is a good time to render a frame. This means, with Wayland V-Sync active, don't draw when this is non-zero.
        private IntPtr _frameCallback;

        // libdl stuff is needed to get a structure pointer from libwayland-client.so.
        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string path, int flags);
        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport("libdl.so.2")]
        private static extern int dlclose(IntPtr handle);

        // libwayland-client stuff for Wayland V-Sync.
        [DllImport("wayland-client")]
        private static extern IntPtr wl_proxy_marshal_constructor(IntPtr proxy, uint opcode, IntPtr _interface, IntPtr zero);
        [DllImport("wayland-client")]
        private static extern int wl_proxy_add_listener(IntPtr proxy, IntPtr implementation, IntPtr data);
        [DllImport("wayland-client")]
        private static extern void wl_proxy_destroy(IntPtr proxy);

        private IntPtr _libwayland_client_handle;
        private IntPtr _wl_surface;
        private IntPtr _wl_callback_interface;

        // The callback listener structure and its corresponding delegate.
        [StructLayout(LayoutKind.Sequential)]
        private struct WlCallbackListener
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            public delegate void d_done(IntPtr data, IntPtr wl_callback, uint callback_data);

            public IntPtr Done;
        };
        private WlCallbackListener.d_done _frameDoneDelegate;
        private WlCallbackListener _listener;

        // A handle to pin the listener so the GC doesn't move it.
        private GCHandle _listenerHandle;

        public SdlGamePlatform(Game game)
            : base(game)
        {
            _game = game;
            _keys = new List<Keys>();
            Keyboard.SetKeys(_keys);

            Sdl.Version sversion;
            Sdl.GetVersion(out sversion);

            Sdl.Major = sversion.Major;
            Sdl.Minor = sversion.Minor;
            Sdl.Patch = sversion.Patch;

            var version = 100 * Sdl.Major + 10 * Sdl.Minor + Sdl.Patch;

            if (version <= 204)
                Debug.WriteLine("Please use SDL 2.0.5 or higher.");

            // Needed so VS can debug the project on Windows
            if (version >= 205 && CurrentPlatform.OS == OS.Windows && Debugger.IsAttached)
                Sdl.SetHint("SDL_WINDOWS_DISABLE_THREAD_NAMING", "1");

            Sdl.Init((int)(
                Sdl.InitFlags.Video |
                Sdl.InitFlags.Joystick |
                Sdl.InitFlags.GameController |
                Sdl.InitFlags.Haptic
            ));

            Sdl.DisableScreenSaver();

            GamePad.InitDatabase();
            Window = _view = new SdlGameWindow(_game);

            WaylandVsync = false;
            _frameCallback = IntPtr.Zero;

            _libwayland_client_handle = IntPtr.Zero;
            _wl_surface = IntPtr.Zero;
            _wl_callback_interface = IntPtr.Zero;

            _frameDoneDelegate = FrameDone;
            _listener = new WlCallbackListener
            {
                Done = Marshal.GetFunctionPointerForDelegate(_frameDoneDelegate)
            };
            // Prevent the GC from moving the listener around, as the C Wayland library will have a pointer to it.
            _listenerHandle = GCHandle.Alloc(_listener, GCHandleType.Pinned);
        }

        public override void BeforeInitialize()
        {
            SdlRunLoop();

            if (CurrentPlatform.OS == OS.Linux)
            {
                // Get the WM type.
                Sdl.GetVersion(out var version);
                var sys = new Sdl.Window.SDL_SysWMinfo
                {
                    version = version
                };

                if (Sdl.Window.GetWindowWMInfo(Window.Handle, ref sys))
                {
                    if (sys.subsystem == Sdl.Window.SysWMType.Wayland)
                    {
                        // Get pointer to wl_surface and the wl_callback_interface to be able to request a frame callback.
                        _wl_surface = sys.wl_surface;

                        _libwayland_client_handle = dlopen("libwayland-client.so", 1);
                        if (_libwayland_client_handle != IntPtr.Zero)
                            _wl_callback_interface = dlsym(_libwayland_client_handle, "wl_callback_interface");
                    }
                }
            }

            base.BeforeInitialize();
        }

        protected override void OnIsMouseVisibleChanged()
        {
            _view.SetCursorVisible(_game.IsMouseVisible);
        }

        internal override void OnPresentationChanged(PresentationParameters pp)
        {
            var displayIndex = Sdl.Window.GetDisplayIndex(Window.Handle);
            var displayName = Sdl.Display.GetDisplayName(displayIndex);
            BeginScreenDeviceChange(pp.IsFullScreen);
            EndScreenDeviceChange(displayName, pp.BackBufferWidth, pp.BackBufferHeight);
        }

        public override void RunLoop()
        {
            Sdl.Window.Show(Window.Handle);

            while (true)
            {
                SdlRunLoop();

                // If frame_callback is non-zero, we're waiting for the Wayland compositor to signal us when it's a good
                // time to draw a frame. With Wayland V-Sync active, suppress drawing until we receive that signal.
                if (WaylandVsync && _frameCallback != IntPtr.Zero)
                    Game.SuppressDraw();

                // If we're on Wayland and haven't requested a frame callback for the next refresh, do so.
                if (_wl_callback_interface != IntPtr.Zero && _frameCallback == IntPtr.Zero)
                {
                    _frameCallback = wl_proxy_marshal_constructor(_wl_surface, 3, _wl_callback_interface, IntPtr.Zero);
                    wl_proxy_add_listener(_frameCallback, _listenerHandle.AddrOfPinnedObject(), IntPtr.Zero);
                }

                Game.Tick();
                Threading.Run();
                GraphicsDevice.DisposeContexts();

                if (_isExiting > 0)
                    break;
            }
        }

        private void SdlRunLoop()
        {
            Sdl.Event ev;

            while (Sdl.PollEvent(out ev) == 1)
            {
                switch (ev.Type)
                {
                    case Sdl.EventType.Quit:
                        _isExiting++;
                        break;
                    case Sdl.EventType.JoyDeviceAdded:
                        Joystick.AddDevice(ev.JoystickDevice.Which);
                        break;
                    case Sdl.EventType.JoyDeviceRemoved:
                        Joystick.RemoveDevice(ev.JoystickDevice.Which);
                        break;
                    case Sdl.EventType.ControllerDeviceRemoved:
                        GamePad.RemoveDevice(ev.ControllerDevice.Which);
                        break;
                    case Sdl.EventType.ControllerButtonUp:
                    case Sdl.EventType.ControllerButtonDown:
                    case Sdl.EventType.ControllerAxisMotion:
                        GamePad.UpdatePacketInfo(ev.ControllerDevice.Which, ev.ControllerDevice.TimeStamp);
                        break;
                    case Sdl.EventType.MouseWheel:
                        const int wheelDelta = 120;
                        Mouse.ScrollY += ev.Wheel.Y * wheelDelta;
                        Mouse.ScrollX += ev.Wheel.X * wheelDelta;
                        break;
                    case Sdl.EventType.MouseMotion:
                        Window.MouseState.X = ev.Motion.X;
                        Window.MouseState.Y = ev.Motion.Y;
                        break;
                    case Sdl.EventType.KeyDown:
                    {
                        var key = KeyboardUtil.ToXna(ev.Key.Keysym.Sym);
                        if (!_keys.Contains(key))
                            _keys.Add(key);
                        char character = (char)ev.Key.Keysym.Sym;
                        _view.OnKeyDown(new InputKeyEventArgs(key));
                        if (char.IsControl(character))
                            _view.OnTextInput(this, new TextInputEventArgs(character, key));
                        break;
                    }
                    case Sdl.EventType.KeyUp:
                    {
                        var key = KeyboardUtil.ToXna(ev.Key.Keysym.Sym);
                        _keys.Remove(key);
                        _view.OnKeyUp(new InputKeyEventArgs(key));
                        break;
                    }
                    case Sdl.EventType.MouseButtonup: 
                    case Sdl.EventType.MouseButtonDown:
                        switch ((Sdl.Mouse.Button) ev.Button.Button)
                        {
                            case Sdl.Mouse.Button.Left:
                                Window.MouseState.LeftButton = ev.Button.State != 0 ? ButtonState.Pressed : ButtonState.Released;
                                break;

                            case Sdl.Mouse.Button.Right:
                                Window.MouseState.RightButton = ev.Button.State != 0 ? ButtonState.Pressed : ButtonState.Released;
                                break;

                            case Sdl.Mouse.Button.Middle:
                                Window.MouseState.MiddleButton = ev.Button.State != 0 ? ButtonState.Pressed : ButtonState.Released;
                                break;

                            case Sdl.Mouse.Button.X1:
                                Window.MouseState.XButton1 = ev.Button.State != 0 ? ButtonState.Pressed : ButtonState.Released;
                                break;

                            case Sdl.Mouse.Button.X2:
                                Window.MouseState.XButton2 = ev.Button.State != 0 ? ButtonState.Pressed : ButtonState.Released;
                                break;

                            default:
                                break;
                        }
                        break;
                    case Sdl.EventType.TextInput:
                        if (_view.IsTextInputHandled)
                        {
                            int len = 0;
                            int utf8character = 0; // using an int to encode multibyte characters longer than 2 bytes
                            byte currentByte = 0;
                            int charByteSize = 0; // UTF8 char lenght to decode
                            int remainingShift = 0;
                            unsafe
                            {
                                while ((currentByte = Marshal.ReadByte((IntPtr)ev.Text.Text, len)) != 0)
                                {
                                    // we're reading the first UTF8 byte, we need to check if it's multibyte
                                    if (charByteSize == 0)
                                    {
                                        if (currentByte < 192)
                                            charByteSize = 1;
                                        else if (currentByte < 224)
                                            charByteSize = 2;
                                        else if (currentByte < 240)
                                            charByteSize = 3;
                                        else
                                            charByteSize = 4;

                                        utf8character = 0;
                                        remainingShift = 4;
                                    }

                                    // assembling the character
                                    utf8character <<= 8;
                                    utf8character |= currentByte;

                                    charByteSize--;
                                    remainingShift--;

                                    if (charByteSize == 0) // finished decoding the current character
                                    {
                                        utf8character <<= remainingShift * 8; // shifting it to full UTF8 scope

                                        // SDL returns UTF8-encoded characters while C# char type is UTF16-encoded (and limited to the 0-FFFF range / does not support surrogate pairs)
                                        // so we need to convert it to Unicode codepoint and check if it's within the supported range
                                        int codepoint = UTF8ToUnicode(utf8character);

                                        if (codepoint >= 0 && codepoint < 0xFFFF)
                                        {
                                            _view.OnTextInput(this, new TextInputEventArgs((char)codepoint, KeyboardUtil.ToXna(codepoint)));
                                            // UTF16 characters beyond 0xFFFF are not supported (and would require a surrogate encoding that is not supported by the char type)
                                        }
                                    }

                                    len++;
                                }
                            }
                        }
                        break;
                    case Sdl.EventType.DropFile:
                        _view.CallFileDrop(Sdl.GetString(ev.Drop.File));
                        break;
                    case Sdl.EventType.WindowEvent:

                        switch (ev.Window.EventID)
                        {
                            case Sdl.Window.EventId.Resized:
                            case Sdl.Window.EventId.SizeChanged:
                                _view.ClientResize(ev.Window.Data1, ev.Window.Data2);
                                break;
                            case Sdl.Window.EventId.FocusGained:
                                IsActive = true;
                                break;
                            case Sdl.Window.EventId.FocusLost:
                                IsActive = false;
                                break;
                            case Sdl.Window.EventId.Moved:
                                _view.Moved();
                                break;
                            case Sdl.Window.EventId.Close:
                                _isExiting++;
                                break;
                        }
                        break;
                }
            }
        }

        private int UTF8ToUnicode(int utf8)
        {
            int
                byte4 = utf8 & 0xFF,
                byte3 = (utf8 >> 8) & 0xFF,
                byte2 = (utf8 >> 16) & 0xFF,
                byte1 = (utf8 >> 24) & 0xFF;

            if (byte1 < 0x80)
                return byte1;
            else if (byte1 < 0xC0)
                return -1;
            else if (byte1 < 0xE0 && byte2 >= 0x80 && byte2 < 0xC0)
                return (byte1 % 0x20) * 0x40 + (byte2 % 0x40);
            else if (byte1 < 0xF0 && byte2 >= 0x80 && byte2 < 0xC0 && byte3 >= 0x80 && byte3 < 0xC0)
                return (byte1 % 0x10) * 0x40 * 0x40 + (byte2 % 0x40) * 0x40 + (byte3 % 0x40);
            else if (byte1 < 0xF8 && byte2 >= 0x80 && byte2 < 0xC0 && byte3 >= 0x80 && byte3 < 0xC0 && byte4 >= 0x80 && byte4 < 0xC0)
                return (byte1 % 0x8) * 0x40 * 0x40 * 0x40 + (byte2 % 0x40) * 0x40 * 0x40 + (byte3 % 0x40) * 0x40 + (byte4 % 0x40);
            else
                return -1;
        }

        public override void StartRunLoop()
        {
            throw new NotSupportedException("The desktop platform does not support asynchronous run loops");
        }

        public override void Exit()
        {
            Interlocked.Increment(ref _isExiting);
        }

        public override bool BeforeUpdate(GameTime gameTime)
        {
            return true;
        }

        public override bool BeforeDraw(GameTime gameTime)
        {
            return true;
        }

        public override void EnterFullScreen()
        {
        }

        public override void ExitFullScreen()
        {
        }

        public override void BeginScreenDeviceChange(bool willBeFullScreen)
        {
            _view.BeginScreenDeviceChange(willBeFullScreen);
        }

        public override void EndScreenDeviceChange(string screenDeviceName, int clientWidth, int clientHeight)
        {
            _view.EndScreenDeviceChange(screenDeviceName, clientWidth, clientHeight);
        }

        public override void Log(string message)
        {
            Console.WriteLine(message);
        }

        private void FrameDone(IntPtr data, IntPtr wl_callback, uint callback_data)
        {
            // Wayland callbacks are (most likely) dispatched within SDL.PollEvent within SdlRunLoop, which happens
            // on the same thread as everything else, so no synchronization is necessary.

            // Destroy the callback (necessary).
            // wl_callback should be the same as frame_callback as we only register a single one.
            wl_proxy_destroy(wl_callback);

            // Set our variable back to zero so we know the callback has fired.
            _frameCallback = IntPtr.Zero;
        }

        public override void Present()
        {
            if (Game.GraphicsDevice != null)
                Game.GraphicsDevice.Present();
        }

        protected override void Dispose(bool disposing)
        {
            if (_frameCallback != IntPtr.Zero)
                wl_proxy_destroy(_frameCallback);
            if (_libwayland_client_handle != IntPtr.Zero)
                dlclose(_libwayland_client_handle);

            if (_view != null)
            {
                _view.Dispose();
                _view = null;

                Joystick.CloseDevices();

                Sdl.Quit();
            }

            base.Dispose(disposing);
        }
    }
}
