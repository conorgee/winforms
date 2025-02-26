﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.System.Com;
using static Interop;
using IAdviseSink = System.Runtime.InteropServices.ComTypes.IAdviseSink;

namespace System.Windows.Forms
{
    public partial class Control
    {
        /// <summary>
        ///  This class holds all of the state data for an ActiveX control and
        ///  supplies the implementation for many of the non-trivial methods.
        /// </summary>
        private unsafe partial class ActiveXImpl : MarshalByRefObject, IWindowTarget
        {
            private const int HiMetricPerInch = 2540;
            private static readonly int s_viewAdviseOnlyOnce = BitVector32.CreateMask();
            private static readonly int s_viewAdvisePrimeFirst = BitVector32.CreateMask(s_viewAdviseOnlyOnce);
            private static readonly int s_eventsFrozen = BitVector32.CreateMask(s_viewAdvisePrimeFirst);
            private static readonly int s_changingExtents = BitVector32.CreateMask(s_eventsFrozen);
            private static readonly int s_saving = BitVector32.CreateMask(s_changingExtents);
            private static readonly int s_isDirty = BitVector32.CreateMask(s_saving);
            private static readonly int s_inPlaceActive = BitVector32.CreateMask(s_isDirty);
            private static readonly int s_inPlaceVisible = BitVector32.CreateMask(s_inPlaceActive);
            private static readonly int s_uiActive = BitVector32.CreateMask(s_inPlaceVisible);
            private static readonly int s_uiDead = BitVector32.CreateMask(s_uiActive);
            private static readonly int s_adjustingRect = BitVector32.CreateMask(s_uiDead);

            private static Point s_logPixels = Point.Empty;
            private static Ole32.OLEVERB[]? s_axVerbs;

            private readonly Control _control;
            private readonly IWindowTarget _controlWindowTarget;
            private Rectangle? _lastClipRect;

            private Ole32.IOleClientSite? _clientSite;
            private Ole32.IOleInPlaceUIWindow? _inPlaceUiWindow;
            private Ole32.IOleInPlaceFrame? _inPlaceFrame;
            private readonly List<IAdviseSink> _adviseList;
            private IAdviseSink? _viewAdviseSink;
            private BitVector32 _activeXState;
            private readonly AmbientProperty[] _ambientProperties;
            private IntPtr _accelTable;
            private short _accelCount = -1;
            private RECT* _adjustRect; // temporary rect used during OnPosRectChange && SetObjectRects

            /// <summary>
            ///  Creates a new ActiveXImpl.
            /// </summary>
            internal ActiveXImpl(Control control)
            {
                _control = control;

                // We replace the control's window target with our own.  We
                // do this so we can handle the UI Dead ambient property.
                _controlWindowTarget = control.WindowTarget;
                control.WindowTarget = this;

                _adviseList = new List<IAdviseSink>();
                _activeXState = new BitVector32();
                _ambientProperties = new AmbientProperty[]
                {
                    new AmbientProperty("Font", Ole32.DispatchID.AMBIENT_FONT),
                    new AmbientProperty("BackColor", Ole32.DispatchID.AMBIENT_BACKCOLOR),
                    new AmbientProperty("ForeColor", Ole32.DispatchID.AMBIENT_FORECOLOR)
                };
            }

            /// <summary>
            ///  Retrieves the ambient back color for the control.
            /// </summary>
            [Browsable(false)]
            [EditorBrowsable(EditorBrowsableState.Advanced)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            internal Color AmbientBackColor
            {
                get
                {
                    AmbientProperty prop = LookupAmbient(Ole32.DispatchID.AMBIENT_BACKCOLOR);

                    if (prop.Empty && GetAmbientProperty(Ole32.DispatchID.AMBIENT_BACKCOLOR, out object? obj) && obj is not null)
                    {
                        try
                        {
                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Object color type={obj.GetType().FullName}");
                            prop.Value = ColorTranslator.FromOle(Convert.ToInt32(obj, CultureInfo.InvariantCulture));
                        }
                        catch (Exception e)
                        {
                            Debug.Fail("Failed to massage ambient back color to a Color", e.ToString());

                            if (ClientUtils.IsCriticalException(e))
                            {
                                throw;
                            }
                        }
                    }

                    return prop.Value is null ? Color.Empty : (Color)prop.Value;
                }
            }

            /// <summary>
            ///  Retrieves the ambient font for the control.
            /// </summary>
            [Browsable(false)]
            [EditorBrowsable(EditorBrowsableState.Advanced)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            internal Font? AmbientFont
            {
                get
                {
                    AmbientProperty prop = LookupAmbient(Ole32.DispatchID.AMBIENT_FONT);

                    if (prop.Empty)
                    {
                        if (GetAmbientProperty(Ole32.DispatchID.AMBIENT_FONT, out object? obj))
                        {
                            try
                            {
                                Debug.Assert(obj is not null, "GetAmbientProperty failed");
                                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Object font type={obj.GetType().FullName}");
                                Ole32.IFont ifont = (Ole32.IFont)obj;
                                prop.Value = Font.FromHfont(ifont.hFont);
                            }
                            catch (Exception e) when (!ClientUtils.IsCriticalException(e))
                            {
                                // Do NULL, so we just defer to the default font
                                prop.Value = null;
                            }
                        }
                    }

                    return (Font?)prop.Value;
                }
            }

            /// <summary>
            ///  Retrieves the ambient back color for the control.
            /// </summary>
            [Browsable(false)]
            [EditorBrowsable(EditorBrowsableState.Advanced)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            internal Color AmbientForeColor
            {
                get
                {
                    AmbientProperty prop = LookupAmbient(Ole32.DispatchID.AMBIENT_FORECOLOR);

                    if (prop.Empty && GetAmbientProperty(Ole32.DispatchID.AMBIENT_FORECOLOR, out object? obj) && obj is not null)
                    {
                        try
                        {
                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Object color type={obj.GetType().FullName}");
                            prop.Value = ColorTranslator.FromOle(Convert.ToInt32(obj, CultureInfo.InvariantCulture));
                        }
                        catch (Exception e)
                        {
                            Debug.Fail("Failed to massage ambient fore color to a Color", e.ToString());

                            if (ClientUtils.IsCriticalException(e))
                            {
                                throw;
                            }
                        }
                    }

                    return prop.Value is null ? Color.Empty : (Color)prop.Value;
                }
            }

            /// <summary>
            ///  Determines if events should be frozen.
            /// </summary>
            [Browsable(false)]
            [EditorBrowsable(EditorBrowsableState.Advanced)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            internal bool EventsFrozen
            {
                get => _activeXState[s_eventsFrozen];
                set => _activeXState[s_eventsFrozen] = value;
            }

            /// <summary>
            ///  Provides access to the parent window handle when we are UI active.
            /// </summary>
            internal HWND HWNDParent { get; private set; }

            /// <summary>
            ///  Retrieves the number of logical pixels per inch on the
            ///  primary monitor.
            /// </summary>
            private static Point LogPixels
            {
                get
                {
                    if (s_logPixels.IsEmpty)
                    {
                        s_logPixels = new Point();
                        using var dc = User32.GetDcScope.ScreenDC;
                        s_logPixels.X = PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.LOGPIXELSX);
                        s_logPixels.Y = PInvoke.GetDeviceCaps(dc, GET_DEVICE_CAPS_INDEX.LOGPIXELSY);
                    }

                    return s_logPixels;
                }
            }

            /// <summary>
            ///  Implements IOleObject::Advise
            /// </summary>
            internal uint Advise(IAdviseSink pAdvSink)
            {
                _adviseList.Add(pAdvSink);
                return (uint)_adviseList.Count;
            }

            /// <summary>
            ///  Implements IOleObject::Close
            /// </summary>
            internal void Close(Ole32.OLECLOSE dwSaveOption)
            {
                if (_activeXState[s_inPlaceActive])
                {
                    InPlaceDeactivate();
                }

                if ((dwSaveOption == Ole32.OLECLOSE.SAVEIFDIRTY || dwSaveOption == Ole32.OLECLOSE.PROMPTSAVE)
                    && _activeXState[s_isDirty])
                {
                    _clientSite?.SaveObject();
                    SendOnSave();
                }
            }

            /// <summary>
            ///  Implements IOleObject::DoVerb
            /// </summary>
            internal unsafe HRESULT DoVerb(
                Ole32.OLEIVERB iVerb,
                MSG* lpmsg,
                Ole32.IOleClientSite pActiveSite,
                int lindex,
                HWND hwndParent,
                RECT* lprcPosRect)
            {
                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, $"AxSource:ActiveXImpl:DoVerb({iVerb})");
                switch (iVerb)
                {
                    case Ole32.OLEIVERB.SHOW:
                    case Ole32.OLEIVERB.INPLACEACTIVATE:
                    case Ole32.OLEIVERB.UIACTIVATE:
                    case Ole32.OLEIVERB.PRIMARY:
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "DoVerb:Show, InPlaceActivate, UIActivate");
                        InPlaceActivate(iVerb);

                        // Now that we're active, send the lpmsg to the control if it is valid.
                        if (lpmsg is null)
                        {
                            break;
                        }

                        Control target = _control;

                        HWND hwnd = lpmsg->hwnd;
                        if (hwnd != _control.HWND && lpmsg->IsMouseMessage())
                        {
                            // Must translate message coordinates over to our HWND.
                            HWND hwndMap = hwnd.IsNull ? hwndParent : hwnd;
                            var pt = new Point
                            {
                                X = PARAM.LOWORD(lpmsg->lParam),
                                Y = PARAM.HIWORD(lpmsg->lParam)
                            };

                            PInvoke.MapWindowPoints(hwndMap, _control, ref pt);

                            // Check to see if this message should really go to a child control, and if so, map the
                            // point into that child's window coordinates.
                            Control? realTarget = target.GetChildAtPoint(pt);
                            if (realTarget is not null && realTarget != target)
                            {
                                pt = WindowsFormsUtils.TranslatePoint(pt, target, realTarget);
                                target = realTarget;
                            }

                            lpmsg->lParam = PARAM.FromPoint(pt);
                        }

#if DEBUG
                        if (CompModSwitches.ActiveX.TraceVerbose)
                        {
                            Message m = Message.Create(lpmsg);
                            Debug.WriteLine($"Valid message pointer passed, sending to control: {m}");
                        }
#endif

                        if (lpmsg->message == (uint)User32.WM.KEYDOWN && lpmsg->wParam == (WPARAM)(nuint)User32.VK.TAB)
                        {
                            target.SelectNextControl(null, ModifierKeys != Keys.Shift, tabStopOnly: true, nested: true, wrap: true);
                        }
                        else
                        {
                            PInvoke.SendMessage(target, (User32.WM)lpmsg->message, lpmsg->wParam, lpmsg->lParam);
                        }

                        break;

                    case Ole32.OLEIVERB.HIDE:
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "DoVerb:Hide");
                        UIDeactivate();
                        InPlaceDeactivate();
                        if (_activeXState[s_inPlaceVisible])
                        {
                            SetInPlaceVisible(false);
                        }

                        break;

                    // All other verbs are not implemented.
                    default:
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "DoVerb:Other");
                        ThrowHr(HRESULT.E_NOTIMPL);
                        break;
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Implements IViewObject2::Draw.
            /// </summary>
            internal unsafe HRESULT Draw(
                Ole32.DVASPECT dwDrawAspect,
                int lindex,
                IntPtr pvAspect,
                Ole32.DVTARGETDEVICE* ptd,
                IntPtr hdcTargetDev,
                IntPtr hdcDraw,
                RECT* prcBounds,
                RECT* lprcWBounds,
                IntPtr pfnContinue,
                uint dwContinue)
            {
                // support the aspects required for multi-pass drawing
                switch (dwDrawAspect)
                {
                    case Ole32.DVASPECT.CONTENT:
                    case Ole32.DVASPECT.OPAQUE:
                    case Ole32.DVASPECT.TRANSPARENT:
                        break;
                    default:
                        return HRESULT.DV_E_DVASPECT;
                }

                // We can paint to an enhanced metafile, but not all GDI / GDI+ is
                // supported on classic metafiles.  We throw VIEW_E_DRAW in the hope that
                // the caller figures it out and sends us a different DC.

                HDC hdc = (HDC)hdcDraw;
                OBJ_TYPE hdcType = (OBJ_TYPE)PInvoke.GetObjectType(hdc);
                if (hdcType == OBJ_TYPE.OBJ_METADC)
                {
                    return HRESULT.VIEW_E_DRAW;
                }

                Point pVp = default;
                Point pW = default;
                Size sWindowExt = default;
                Size sViewportExt = default;
                HDC_MAP_MODE iMode = HDC_MAP_MODE.MM_TEXT;

                if (!_control.IsHandleCreated)
                {
                    _control.CreateHandle();
                }

                // If they didn't give us a rectangle, just copy over ours.
                if (prcBounds is not null)
                {
                    RECT rc = *prcBounds;

                    // To draw to a given rect, we scale the DC in such a way as to make the values it takes match our
                    // own happy MM_TEXT. Then, we back-convert prcBounds so that we convert it to this coordinate
                    // system. This puts us in the most similar coordinates as we currently use.
                    Point p1 = new(rc.left, rc.top);
                    Point p2 = new(rc.right - rc.left, rc.bottom - rc.top);
                    PInvoke.LPtoDP(hdc, new Point[] { p1, p2 }.AsSpan());

                    iMode = (HDC_MAP_MODE)PInvoke.SetMapMode(hdc, HDC_MAP_MODE.MM_ANISOTROPIC);
                    PInvoke.SetWindowOrgEx(hdc, 0, 0, &pW);
                    PInvoke.SetWindowExtEx(hdc, _control.Width, _control.Height, (SIZE*)&sWindowExt);
                    PInvoke.SetViewportOrgEx(hdc, p1.X, p1.Y, &pVp);
                    PInvoke.SetViewportExtEx(hdc, p2.X, p2.Y, (SIZE*)&sViewportExt);
                }

                // Now do the actual drawing.  We must ask all of our children to draw as well.
                try
                {
                    nint flags = (nint)(User32.PRF.CHILDREN | User32.PRF.CLIENT | User32.PRF.ERASEBKGND | User32.PRF.NONCLIENT);
                    if (hdcType != OBJ_TYPE.OBJ_ENHMETADC)
                    {
                        PInvoke.SendMessage(_control, User32.WM.PRINT, (WPARAM)hdc, (LPARAM)flags);
                    }
                    else
                    {
                        _control.PrintToMetaFile(hdc, flags);
                    }
                }
                finally
                {
                    // And clean up the DC
                    if (prcBounds is not null)
                    {
                        PInvoke.SetWindowOrgEx(hdc, pW.X, pW.Y, lppt: null);
                        PInvoke.SetWindowExtEx(hdc, sWindowExt.Width, sWindowExt.Height, lpsz: null);
                        PInvoke.SetViewportOrgEx(hdc, pVp.X, pVp.Y, lppt: null);
                        PInvoke.SetViewportExtEx(hdc, sViewportExt.Width, sViewportExt.Height, lpsz: null);
                        PInvoke.SetMapMode(hdc, iMode);
                    }
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Returns a new verb enumerator.
            /// </summary>
            internal static HRESULT EnumVerbs(out Ole32.IEnumOLEVERB ppEnumOleVerb)
            {
                if (s_axVerbs is null)
                {
                    Ole32.OLEVERB verbShow = new();
                    Ole32.OLEVERB verbInplaceActivate = new();
                    Ole32.OLEVERB verbUIActivate = new();
                    Ole32.OLEVERB verbHide = new();
                    Ole32.OLEVERB verbPrimary = new();
                    Ole32.OLEVERB verbProperties = new();

                    verbShow.lVerb = Ole32.OLEIVERB.SHOW;
                    verbInplaceActivate.lVerb = Ole32.OLEIVERB.INPLACEACTIVATE;
                    verbUIActivate.lVerb = Ole32.OLEIVERB.UIACTIVATE;
                    verbHide.lVerb = Ole32.OLEIVERB.HIDE;
                    verbPrimary.lVerb = Ole32.OLEIVERB.PRIMARY;
                    verbProperties.lVerb = Ole32.OLEIVERB.PROPERTIES;
                    verbProperties.lpszVerbName = SR.AXProperties;
                    verbProperties.grfAttribs = Ole32.OLEVERBATTRIB.ONCONTAINERMENU;

                    s_axVerbs = new Ole32.OLEVERB[]
                    {
                        verbShow,
                        verbInplaceActivate,
                        verbUIActivate,
                        verbHide,
                        verbPrimary
                    };
                }

                ppEnumOleVerb = new ActiveXVerbEnum(s_axVerbs);
                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Converts the given string to a byte array.
            /// </summary>
            private static byte[] FromBase64WrappedString(string text)
            {
                if (text.IndexOfAny(new char[] { ' ', '\r', '\n' }) != -1)
                {
                    StringBuilder sb = new StringBuilder(text.Length);
                    for (int i = 0; i < text.Length; i++)
                    {
                        switch (text[i])
                        {
                            case ' ':
                            case '\r':
                            case '\n':
                                break;
                            default:
                                sb.Append(text[i]);
                                break;
                        }
                    }

                    return Convert.FromBase64String(sb.ToString());
                }
                else
                {
                    return Convert.FromBase64String(text);
                }
            }

            /// <summary>
            ///  Implements IViewObject2::GetAdvise.
            /// </summary>
            internal unsafe HRESULT GetAdvise(Ole32.DVASPECT* pAspects, Ole32.ADVF* pAdvf, IAdviseSink?[] ppAdvSink)
            {
                if (pAspects is not null)
                {
                    *pAspects = Ole32.DVASPECT.CONTENT;
                }

                if (pAdvf is not null)
                {
                    *pAdvf = 0;

                    if (_activeXState[s_viewAdviseOnlyOnce])
                    {
                        *pAdvf |= Ole32.ADVF.ONLYONCE;
                    }

                    if (_activeXState[s_viewAdvisePrimeFirst])
                    {
                        *pAdvf |= Ole32.ADVF.PRIMEFIRST;
                    }
                }

                if (ppAdvSink is not null)
                {
                    ppAdvSink[0] = _viewAdviseSink;
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Helper function to retrieve an ambient property.  Returns false if the
            ///  property wasn't found.
            /// </summary>
            private bool GetAmbientProperty(Ole32.DispatchID dispid, out object? obj)
            {
                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "AxSource:GetAmbientProperty");
                Debug.Indent();

                if (_clientSite is Oleaut32.IDispatch disp)
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "clientSite implements IDispatch");

                    DISPPARAMS dispParams = new();
                    object[] pvt = new object[1];
                    Guid g = Guid.Empty;
                    HRESULT hr = disp.Invoke(
                        dispid,
                        &g,
                        PInvoke.LCID.USER_DEFAULT,
                        DISPATCH_FLAGS.DISPATCH_PROPERTYGET,
                        &dispParams,
                        pvt,
                        null,
                        null);

                    if (hr.Succeeded)
                    {
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"IDispatch::Invoke succeeded. VT={pvt[0].GetType().FullName}");
                        obj = pvt[0];
                        Debug.Unindent();
                        return true;
                    }

                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"IDispatch::Invoke failed. HR: 0x{hr:X}");
                }

                Debug.Unindent();

                obj = null;

                return false;
            }

            /// <summary>
            ///  Implements IOleObject::GetClientSite.
            /// </summary>
            internal Ole32.IOleClientSite? GetClientSite() => _clientSite;

            internal unsafe HRESULT GetControlInfo(Ole32.CONTROLINFO* pCI)
            {
                if (_accelCount == -1)
                {
                    List<char> mnemonicList = new();
                    GetMnemonicList(_control, mnemonicList);

                    _accelCount = (short)mnemonicList.Count;

                    if (_accelCount > 0)
                    {
                        // In the worst case we may have two accelerators per mnemonic:  one lower case and
                        // one upper case, hence the * 2 below.
                        var accelerators = new User32.ACCEL[_accelCount * 2];
                        Debug.Indent();

                        ushort cmd = 0;
                        _accelCount = 0;

                        foreach (char ch in mnemonicList)
                        {
                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "Mnemonic: " + ch.ToString());

                            short scan = PInvoke.VkKeyScan(ch);
                            ushort key = (ushort)(scan & 0x00FF);
                            if (ch >= 'A' && ch <= 'Z')
                            {
                                // Lower case letter
                                accelerators[_accelCount++] = new User32.ACCEL
                                {
                                    fVirt = User32.AcceleratorFlags.FALT | User32.AcceleratorFlags.FVIRTKEY,
                                    key = key,
                                    cmd = cmd
                                };

                                // Upper case letter
                                accelerators[_accelCount++] = new User32.ACCEL
                                {
                                    fVirt = User32.AcceleratorFlags.FALT | User32.AcceleratorFlags.FVIRTKEY | User32.AcceleratorFlags.FSHIFT,
                                    key = key,
                                    cmd = cmd
                                };
                            }
                            else
                            {
                                // Some non-printable character.
                                User32.AcceleratorFlags virt = User32.AcceleratorFlags.FALT | User32.AcceleratorFlags.FVIRTKEY;
                                if ((scan & 0x0100) != 0)
                                {
                                    virt |= User32.AcceleratorFlags.FSHIFT;
                                }

                                accelerators[_accelCount++] = new User32.ACCEL
                                {
                                    fVirt = virt,
                                    key = key,
                                    cmd = cmd
                                };
                            }

                            cmd++;
                        }

                        Debug.Unindent();

                        // Now create an accelerator table and then free our memory.

                        if (_accelTable != IntPtr.Zero)
                        {
                            PInvoke.DestroyAcceleratorTable(new HandleRef<HACCEL>(this, (HACCEL)_accelTable));
                            _accelTable = IntPtr.Zero;
                        }

                        fixed (User32.ACCEL* pAccelerators = accelerators)
                        {
                            _accelTable = User32.CreateAcceleratorTableW(pAccelerators, _accelCount);
                        }
                    }
                }

                pCI->cAccel = (ushort)_accelCount;
                pCI->hAccel = _accelTable;
                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Implements IOleObject::GetExtent.
            /// </summary>
            internal unsafe void GetExtent(Ole32.DVASPECT dwDrawAspect, Size* pSizel)
            {
                if ((dwDrawAspect & Ole32.DVASPECT.CONTENT) != 0)
                {
                    Size size = _control.Size;

                    Point pt = PixelToHiMetric(size.Width, size.Height);
                    pSizel->Width = pt.X;
                    pSizel->Height = pt.Y;
                }
                else
                {
                    ThrowHr(HRESULT.DV_E_DVASPECT);
                }
            }

            /// <summary>
            ///  Searches the control hierarchy of the given control and adds
            ///  the mnemonics for each control to mnemonicList.  Each mnemonic
            ///  is added as a char to the list.
            /// </summary>
            private void GetMnemonicList(Control control, List<char> mnemonicList)
            {
                // Get the mnemonic for our control
                char mnemonic = WindowsFormsUtils.GetMnemonic(control.Text, true);
                if (mnemonic != 0)
                {
                    mnemonicList.Add(mnemonic);
                }

                // And recurse for our children.
                foreach (Control c in control.Controls)
                {
                    if (c is not null)
                    {
                        GetMnemonicList(c, mnemonicList);
                    }
                }
            }

            /// <summary>
            ///  Name to use for a stream: use the control's type name (max 31 chars, use the end chars
            ///  if it's longer than that)
            /// </summary>
            private string GetStreamName()
            {
                string streamName = _control.GetType().FullName!;
                int len = streamName.Length;
                if (len > 31)
                {
                    // The max allowed length of the stream name is 31.
                    streamName = streamName.Substring(len - 31);
                }

                return streamName;
            }

            /// <summary>
            ///  Implements IOleWindow::GetWindow
            /// </summary>
            internal unsafe HRESULT GetWindow(HWND* phwnd)
            {
                if (phwnd is null)
                {
                    return HRESULT.E_POINTER;
                }

                if (!_activeXState[s_inPlaceActive])
                {
                    *phwnd = HWND.Null;
                    return HRESULT.E_FAIL;
                }

                *phwnd = (HWND)_control.Handle;
                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Converts coordinates in HiMetric to pixels.  Used for ActiveX sourcing.
            /// </summary>
            private static Point HiMetricToPixel(int x, int y)
            {
                Point pt = new Point
                {
                    X = (LogPixels.X * x + HiMetricPerInch / 2) / HiMetricPerInch,
                    Y = (LogPixels.Y * y + HiMetricPerInch / 2) / HiMetricPerInch
                };
                return pt;
            }

            /// <summary>
            ///  In place activates this Object.
            /// </summary>
            internal unsafe void InPlaceActivate(Ole32.OLEIVERB verb)
            {
                // If we don't have a client site, then there's not much to do.
                // We also punt if this isn't an in-place site, since we can't
                // go active then.
                if (!(_clientSite is Ole32.IOleInPlaceSite inPlaceSite))
                {
                    return;
                }

                // If we're not already active, go and do it.
                if (!_activeXState[s_inPlaceActive])
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "\tActiveXImpl:InPlaceActivate --> inplaceactive");

                    HRESULT hr = inPlaceSite.CanInPlaceActivate();
                    if (hr != HRESULT.S_OK)
                    {
                        if (hr.Succeeded)
                        {
                            hr = HRESULT.E_FAIL;
                        }

                        ThrowHr(hr);
                    }

                    inPlaceSite.OnInPlaceActivate();

                    _activeXState[s_inPlaceActive] = true;
                }

                // And if we're not visible, do that too.
                if (!_activeXState[s_inPlaceVisible])
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "\tActiveXImpl:InPlaceActivate --> inplacevisible");
                    Ole32.OLEINPLACEFRAMEINFO inPlaceFrameInfo = new()
                    {
                        cb = (uint)sizeof(Ole32.OLEINPLACEFRAMEINFO)
                    };

                    // We are entering a secure context here.
                    HWND hwndParent = default;
                    HRESULT hr = inPlaceSite.GetWindow((nint*)&hwndParent);
                    if (!hr.Succeeded)
                    {
                        ThrowHr(hr);
                    }

                    var posRect = new RECT();
                    var clipRect = new RECT();

                    if (_inPlaceUiWindow is not null && Marshal.IsComObject(_inPlaceUiWindow))
                    {
                        Marshal.ReleaseComObject(_inPlaceUiWindow);
                        _inPlaceUiWindow = null;
                    }

                    if (_inPlaceFrame is not null && Marshal.IsComObject(_inPlaceFrame))
                    {
                        Marshal.ReleaseComObject(_inPlaceFrame);
                        _inPlaceFrame = null;
                    }

                    inPlaceSite.GetWindowContext(
                        out Ole32.IOleInPlaceFrame pFrame,
                        out Ole32.IOleInPlaceUIWindow? pWindow,
                        &posRect,
                        &clipRect,
                        &inPlaceFrameInfo);

                    SetObjectRects(&posRect, &clipRect);

                    _inPlaceFrame = pFrame;
                    _inPlaceUiWindow = pWindow;

                    // We are parenting ourselves
                    // directly to the host window.  The host must
                    // implement the ambient property
                    // DISPID_AMBIENT_MESSAGEREFLECT.
                    // If it doesn't, that means that the host
                    // won't reflect messages back to us.
                    HWNDParent = hwndParent;
                    if (PInvoke.SetParent(_control, hwndParent).IsNull)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), SR.Win32SetParentFailed);
                    }

                    // Now create our handle if it hasn't already been done.
                    _control.CreateControl();

                    _clientSite.ShowObject();

                    SetInPlaceVisible(true);
                    Debug.Assert(_activeXState[s_inPlaceVisible], "Failed to set inplacevisible");
                }

                // if we weren't asked to UIActivate, then we're done.
                if (verb != Ole32.OLEIVERB.PRIMARY && verb != Ole32.OLEIVERB.UIACTIVATE)
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "\tActiveXImpl:InPlaceActivate --> not becoming UIActive");
                    return;
                }

                // if we're not already UI active, do sow now.
                if (!_activeXState[s_uiActive])
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "\tActiveXImpl:InPlaceActivate --> uiactive");
                    _activeXState[s_uiActive] = true;

                    // inform the container of our intent
                    inPlaceSite.OnUIActivate();

                    // take the focus  [which is what UI Activation is all about !]
                    if (!_control.ContainsFocus)
                    {
                        _control.Focus();
                    }

                    // set ourselves up in the host.
                    Debug.Assert(_inPlaceFrame is not null, "Setting us to visible should have created the in place frame");
                    _inPlaceFrame.SetActiveObject(_control, null);
                    _inPlaceUiWindow?.SetActiveObject(_control, null);

                    // we have to explicitly say we don't wany any border space.
                    HRESULT hr = _inPlaceFrame.SetBorderSpace(null);
                    if (!hr.Succeeded && hr != HRESULT.OLE_E_INVALIDRECT &&
                        hr != HRESULT.INPLACE_E_NOTOOLSPACE && hr != HRESULT.E_NOTIMPL)
                    {
                        Marshal.ThrowExceptionForHR((int)hr);
                    }

                    if (_inPlaceUiWindow is not null)
                    {
                        hr = _inPlaceFrame.SetBorderSpace(null);
                        if (!hr.Succeeded && hr != HRESULT.OLE_E_INVALIDRECT &&
                            hr != HRESULT.INPLACE_E_NOTOOLSPACE && hr != HRESULT.E_NOTIMPL)
                        {
                            Marshal.ThrowExceptionForHR((int)hr);
                        }
                    }
                }
                else
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceVerbose, "\tActiveXImpl:InPlaceActivate --> already uiactive");
                }
            }

            /// <summary>
            ///  Implements IOleInPlaceObject::InPlaceDeactivate.
            /// </summary>
            internal HRESULT InPlaceDeactivate()
            {
                // Only do this if we're already in place active.
                if (!_activeXState[s_inPlaceActive])
                {
                    return HRESULT.S_OK;
                }

                // Deactivate us if we're UI active
                if (_activeXState[s_uiActive])
                {
                    UIDeactivate();
                }

                // Some containers may call into us to save, and if we're still
                // active we will try to deactivate and recurse back into the container.
                // So, set the state bits here first.
                _activeXState[s_inPlaceActive] = false;
                _activeXState[s_inPlaceVisible] = false;

                // Notify our site of our deactivation.
                if (_clientSite is Ole32.IOleInPlaceSite oleClientSite)
                {
                    oleClientSite.OnInPlaceDeactivate();
                }

                _control.Visible = false;
                HWNDParent = default;

                if (_inPlaceUiWindow is not null && Marshal.IsComObject(_inPlaceUiWindow))
                {
                    Marshal.ReleaseComObject(_inPlaceUiWindow);
                    _inPlaceUiWindow = null;
                }

                if (_inPlaceFrame is not null && Marshal.IsComObject(_inPlaceFrame))
                {
                    Marshal.ReleaseComObject(_inPlaceFrame);
                    _inPlaceFrame = null;
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Implements IPersistStreamInit::IsDirty.
            /// </summary>
            internal HRESULT IsDirty()
            {
                if (_activeXState[s_isDirty])
                {
                    return HRESULT.S_OK;
                }

                return HRESULT.S_FALSE;
            }

            /// <summary>
            ///  Implements IPersistStorage::Load
            /// </summary>
            internal void Load(Ole32.IStorage stg)
            {
                Ole32.IStream stream;
                try
                {
                    stream = stg.OpenStream(
                        GetStreamName(),
                        IntPtr.Zero,
                        Ole32.STGM.READ | Ole32.STGM.SHARE_EXCLUSIVE,
                        0);
                }
                catch (COMException e) when (e.ErrorCode == (int)HRESULT.STG_E_FILENOTFOUND)
                {
                    // For backward compatibility: We were earlier using GetType().FullName
                    // as the stream name in v1. Lets see if a stream by that name exists.
                    stream = stg.OpenStream(
                        GetType().FullName!,
                        IntPtr.Zero,
                        Ole32.STGM.READ | Ole32.STGM.SHARE_EXCLUSIVE,
                        0);
                }

                Load(stream);
                if (Marshal.IsComObject(stg))
                {
                    Marshal.ReleaseComObject(stg);
                }
            }

            /// <summary>
            ///  Implements IPersistStreamInit::Load
            /// </summary>
            internal void Load(Ole32.IStream stream)
            {
                // We do everything through property bags because we support full fidelity
                // in them.  So, load through that method.
                PropertyBagStream bag = new PropertyBagStream();
                bag.Read(stream);
                Load(bag, null);

                if (Marshal.IsComObject(stream))
                {
                    Marshal.ReleaseComObject(stream);
                }
            }

            /// <summary>
            ///  Implements IPersistPropertyBag::Load
            /// </summary>
            internal unsafe void Load(Oleaut32.IPropertyBag pPropBag, Oleaut32.IErrorLog? pErrorLog)
            {
                PropertyDescriptorCollection props = TypeDescriptor.GetProperties(
                    _control,
                    new Attribute[] { DesignerSerializationVisibilityAttribute.Visible });

                for (int i = 0; i < props.Count; i++)
                {
                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Loading property {props[i].Name}");

                    try
                    {
                        HRESULT hr = pPropBag.Read(props[i].Name, out object? obj, pErrorLog);
                        if (!hr.Succeeded || obj is null)
                        {
                            continue;
                        }

                        Debug.Indent();
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "Property was in bag");

                        string? errorString = null;
                        HRESULT errorCode = HRESULT.S_OK;

                        try
                        {
                            string? value = obj as string ?? Convert.ToString(obj, CultureInfo.InvariantCulture);

                            if (value is null)
                            {
                                Debug.Fail($"Couldn't convert {props[i].Name} to string.");
                                continue;
                            }

                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "It's a standard property");

                            // Not a resource property.  Use TypeConverters to convert the string back to the data type.  We do
                            // not check for CanConvertFrom here -- we the conversion fails the type converter will throw,
                            // and we will log it into the COM error log.
                            TypeConverter converter = props[i].Converter;
                            Debug.Assert(converter is not null, $"No type converter for property '{props[i].Name}' on class {_control.GetType().FullName}");

                            // Check to see if the type converter can convert from a string.  If it can,.
                            // use that as it is the best format for IPropertyBag.  Otherwise, check to see
                            // if it can convert from a byte array.  If it can, get the string, decode it
                            // to a byte array, and then set the value.
                            object? newValue = null;

                            if (converter.CanConvertFrom(typeof(string)))
                            {
                                newValue = converter.ConvertFromInvariantString(value);
                            }
                            else if (converter.CanConvertFrom(typeof(byte[])))
                            {
                                newValue = converter.ConvertFrom(null, CultureInfo.InvariantCulture, FromBase64WrappedString(value));
                            }

                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Converter returned {newValue}");
                            props[i].SetValue(_control, newValue);
                        }
                        catch (Exception e)
                        {
                            errorString = e.ToString();
                            errorCode = e is ExternalException ee ? (HRESULT)ee.ErrorCode : HRESULT.E_FAIL;
                        }

                        if (errorString is not null)
                        {
                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Exception converting property: {errorString}");
                            if (pErrorLog is not null)
                            {
                                using BSTR bstrSource = new(_control.GetType().FullName!);
                                using BSTR bstrDescription = new(errorString);
                                EXCEPINFO err = new()
                                {
                                    bstrSource = bstrSource,
                                    bstrDescription = bstrDescription,
                                    scode = errorCode
                                };
                                pErrorLog.AddError(props[i].Name, &err);
                            }
                        }

                        Debug.Unindent();
                    }
                    catch (Exception ex)
                    {
                        Debug.Fail("Unexpected failure reading property", ex.ToString());

                        if (ClientUtils.IsCriticalException(ex))
                        {
                            throw;
                        }
                    }
                }

                if (Marshal.IsComObject(pPropBag))
                {
                    Marshal.ReleaseComObject(pPropBag);
                }
            }

            /// <summary>
            ///  Simple lookup to find the AmbientProperty corresponding to the given
            ///  dispid.
            /// </summary>
            private AmbientProperty LookupAmbient(Ole32.DispatchID dispid)
            {
                for (int i = 0; i < _ambientProperties.Length; i++)
                {
                    if (_ambientProperties[i].DispID == dispid)
                    {
                        return _ambientProperties[i];
                    }
                }

                Debug.Fail($"No ambient property for dispid {dispid}");
                return _ambientProperties[0];
            }

            /// <summary>
            ///  Merges the input region with the current clipping region, if any.
            /// </summary>
            internal Region MergeRegion(Region region)
            {
                if (_lastClipRect.HasValue)
                {
                    region.Exclude(_lastClipRect.Value);
                }

                return region;
            }

            private static void CallParentPropertyChanged(Control control, string propName)
            {
                switch (propName)
                {
                    case "BackColor":
                        control.OnParentBackColorChanged(EventArgs.Empty);
                        break;
                    case "BackgroundImage":
                        control.OnParentBackgroundImageChanged(EventArgs.Empty);
                        break;
                    case "BindingContext":
                        control.OnParentBindingContextChanged(EventArgs.Empty);
                        break;
                    case "Enabled":
                        control.OnParentEnabledChanged(EventArgs.Empty);
                        break;
                    case "Font":
                        control.OnParentFontChanged(EventArgs.Empty);
                        break;
                    case "ForeColor":
                        control.OnParentForeColorChanged(EventArgs.Empty);
                        break;
                    case "RightToLeft":
                        control.OnParentRightToLeftChanged(EventArgs.Empty);
                        break;
                    case "Visible":
                        control.OnParentVisibleChanged(EventArgs.Empty);
                        break;
                    default:
                        Debug.Fail("There is no property change notification for: " + propName + " on Control.");
                        break;
                }
            }

            /// <summary>
            ///  Implements IOleControl::OnAmbientPropertyChanged
            /// </summary>
            internal void OnAmbientPropertyChange(Ole32.DispatchID dispID)
            {
                if (dispID != Ole32.DispatchID.UNKNOWN)
                {
                    // Look for a specific property that has changed.
                    for (int i = 0; i < _ambientProperties.Length; i++)
                    {
                        if (_ambientProperties[i].DispID == dispID)
                        {
                            _ambientProperties[i].ResetValue();
                            CallParentPropertyChanged(_control, _ambientProperties[i].Name);
                            return;
                        }
                    }

                    object? obj;

                    // Special properties that we care about
                    switch (dispID)
                    {
                        case Ole32.DispatchID.AMBIENT_UIDEAD:
                            if (GetAmbientProperty(Ole32.DispatchID.AMBIENT_UIDEAD, out obj))
                            {
                                _activeXState[s_uiDead] = (bool)obj!;
                            }

                            break;

                        case Ole32.DispatchID.AMBIENT_DISPLAYASDEFAULT:
                            if (_control is IButtonControl ibuttonControl && GetAmbientProperty(Ole32.DispatchID.AMBIENT_DISPLAYASDEFAULT, out obj))
                            {
                                ibuttonControl.NotifyDefault((bool)obj!);
                            }

                            break;
                    }
                }
                else
                {
                    // Invalidate all properties.  Ideally we should be checking each one, but
                    // that's pretty expensive too.
                    for (int i = 0; i < _ambientProperties.Length; i++)
                    {
                        _ambientProperties[i].ResetValue();
                        CallParentPropertyChanged(_control, _ambientProperties[i].Name);
                    }
                }
            }

            /// <summary>
            ///  Implements IOleInPlaceActiveObject::OnDocWindowActivate.
            /// </summary>
            internal void OnDocWindowActivate(BOOL fActivate)
            {
                if (_activeXState[s_uiActive] && fActivate && _inPlaceFrame is not null)
                {
                    // we have to explicitly say we don't wany any border space.
                    HRESULT hr = _inPlaceFrame.SetBorderSpace(null);
                    if (!hr.Succeeded && hr != HRESULT.INPLACE_E_NOTOOLSPACE && hr != HRESULT.E_NOTIMPL)
                    {
                        Marshal.ThrowExceptionForHR((int)hr);
                    }
                }
            }

            /// <summary>
            ///  Called by Control when it gets the focus.
            /// </summary>
            internal void OnFocus(bool focus)
            {
                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"AXSource: SetFocus:  {focus}");
                if (_activeXState[s_inPlaceActive] && _clientSite is Ole32.IOleControlSite oleSite)
                {
                    oleSite.OnFocus(focus);
                }

                if (focus && _activeXState[s_inPlaceActive] && !_activeXState[s_uiActive])
                {
                    InPlaceActivate(Ole32.OLEIVERB.UIACTIVATE);
                }
            }

            /// <summary>
            ///  Converts coordinates in pixels to HiMetric.
            /// </summary>
            private static Point PixelToHiMetric(int x, int y)
            {
                Point pt = new Point
                {
                    X = (HiMetricPerInch * x + (LogPixels.X >> 1)) / LogPixels.X,
                    Y = (HiMetricPerInch * y + (LogPixels.Y >> 1)) / LogPixels.Y
                };
                return pt;
            }

            /// <summary>
            ///  Our implementation of IQuickActivate::QuickActivate
            /// </summary>
            internal unsafe HRESULT QuickActivate(Ole32.QACONTAINER pQaContainer, Ole32.QACONTROL* pQaControl)
            {
                if (pQaControl is null)
                {
                    return HRESULT.E_FAIL;
                }

                // Hookup our ambient colors
                AmbientProperty prop = LookupAmbient(Ole32.DispatchID.AMBIENT_BACKCOLOR);
                prop.Value = ColorTranslator.FromOle(unchecked((int)pQaContainer.colorBack));

                prop = LookupAmbient(Ole32.DispatchID.AMBIENT_FORECOLOR);
                prop.Value = ColorTranslator.FromOle(unchecked((int)pQaContainer.colorFore));

                // And our ambient font
                if (pQaContainer.pFont is not null)
                {
                    prop = LookupAmbient(Ole32.DispatchID.AMBIENT_FONT);

                    try
                    {
                        prop.Value = Font.FromHfont(pQaContainer.pFont.hFont);
                    }
                    catch (Exception e) when (!ClientUtils.IsCriticalException(e))
                    {
                        // Do NULL, so we just defer to the default font
                        prop.Value = null;
                    }
                }

                // Now use the rest of the goo that we got passed in.
                pQaControl->cbSize = (uint)sizeof(Ole32.QACONTROL);

                SetClientSite(pQaContainer.pClientSite);

                if (pQaContainer.pAdviseSink is not null)
                {
                    SetAdvise(Ole32.DVASPECT.CONTENT, 0, pQaContainer.pAdviseSink);
                }

                Ole32.OLEMISC status = 0;
                ((Ole32.IOleObject)_control).GetMiscStatus(Ole32.DVASPECT.CONTENT, &status);
                pQaControl->dwMiscStatus = status;

                // Advise the event sink so VB6 can catch events raised from UserControls.
                // VB6 expects the control to do this during IQuickActivate, otherwise it will not hook events at runtime.
                // We will do this if all of the following are true:
                //  1. The container (e.g., vb6) has supplied an event sink
                //  2. The control is a UserControl (this is only to limit the scope of the changed behavior)
                //  3. The UserControl has indicated it wants to expose events to COM via the ComSourceInterfacesAttribute
                // Note that the AdviseHelper handles some non-standard COM interop that is required in order to access
                // the events on the CLR-supplied CCW (COM-callable Wrapper.

                if ((pQaContainer.pUnkEventSink is not null) && (_control is UserControl))
                {
                    // Check if this control exposes events to COM.
                    Type? eventInterface = GetDefaultEventsInterface(_control.GetType());

                    if (eventInterface is not null)
                    {
                        try
                        {
                            // For the default source interface, call IConnectionPoint.Advise with the supplied event sink.
                            // This is easier said than done. See notes in AdviseHelper.AdviseConnectionPoint.
                            AdviseHelper.AdviseConnectionPoint(_control, pQaContainer.pUnkEventSink, eventInterface, out pQaControl->dwEventCookie);
                        }
                        catch (Exception e) when (!ClientUtils.IsCriticalException(e))
                        {
                        }
                    }
                }

                if (pQaContainer.pPropertyNotifySink is not null && Marshal.IsComObject(pQaContainer.pPropertyNotifySink))
                {
                    Marshal.ReleaseComObject(pQaContainer.pPropertyNotifySink);
                }

                if (pQaContainer.pUnkEventSink is not null && Marshal.IsComObject(pQaContainer.pUnkEventSink))
                {
                    Marshal.ReleaseComObject(pQaContainer.pUnkEventSink);
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Return the default COM events interface declared on a .NET class.
            ///  This looks for the ComSourceInterfacesAttribute and returns the .NET
            ///  interface type of the first interface declared.
            /// </summary>
            private static Type? GetDefaultEventsInterface(Type controlType)
            {
                Type? eventInterface = null;
                object[] custom = controlType.GetCustomAttributes(typeof(ComSourceInterfacesAttribute), false);

                if (custom.Length > 0)
                {
                    ComSourceInterfacesAttribute coms = (ComSourceInterfacesAttribute)custom[0];
                    string eventName = coms.Value.Split(new char[] { '\0' })[0];
                    eventInterface = controlType.Module.Assembly.GetType(eventName, false);
                    eventInterface ??= Type.GetType(eventName, false);
                }

                return eventInterface;
            }

            /// <summary>
            ///  Implements IPersistStorage::Save
            /// </summary>
            internal void Save(Ole32.IStorage stg, BOOL fSameAsLoad)
            {
                Ole32.IStream stream = stg.CreateStream(
                    GetStreamName(),
                    Ole32.STGM.WRITE | Ole32.STGM.SHARE_EXCLUSIVE | Ole32.STGM.CREATE,
                    0,
                    0);
                Debug.Assert(stream is not null, "Stream should be non-null, or an exception should have been thrown.");

                Save(stream, true);
                Marshal.ReleaseComObject(stream);
            }

            /// <summary>
            ///  Implements IPersistStreamInit::Save
            /// </summary>
            internal void Save(Ole32.IStream stream, BOOL fClearDirty)
            {
                // We do everything through property bags because we support full fidelity
                // in them.  So, save through that method.
                PropertyBagStream bag = new PropertyBagStream();
                Save(bag, fClearDirty, false);
                bag.Write(stream);

                if (Marshal.IsComObject(stream))
                {
                    Marshal.ReleaseComObject(stream);
                }
            }

            /// <summary>
            ///  Implements IPersistPropertyBag::Save
            /// </summary>
            internal void Save(Oleaut32.IPropertyBag pPropBag, BOOL fClearDirty, BOOL fSaveAllProperties)
            {
                PropertyDescriptorCollection props = TypeDescriptor.GetProperties(
                    _control,
                    new Attribute[] { DesignerSerializationVisibilityAttribute.Visible });

                for (int i = 0; i < props.Count; i++)
                {
                    if (!fSaveAllProperties && !props[i].ShouldSerializeValue(_control))
                    {
                        continue;
                    }

                    Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Saving property {props[i].Name}");

                    object? value;

                    // Not a resource property.  Persist this using standard type converters.
                    TypeConverter converter = props[i].Converter;
                    Debug.Assert(
                        converter is not null,
                        $"No type converter for property '{props[i].Name}' on class {_control.GetType().FullName}");

                    if (converter.CanConvertFrom(typeof(string)))
                    {
                        VARIANT variant = new();
                        value = converter.ConvertToInvariantString(props[i].GetValue(_control));
                        Marshal.GetNativeVariantForObject(value, (nint)(void*)&variant);
                        pPropBag.Write(props[i].Name, ref value!);
                    }
                    else if (converter.CanConvertFrom(typeof(byte[])))
                    {
                        byte[] data = (byte[])converter.ConvertTo(
                            context: null,
                            CultureInfo.InvariantCulture,
                            props[i].GetValue(_control),
                            typeof(byte[]))!;

                        value = Convert.ToBase64String(data);
                        pPropBag.Write(props[i].Name, ref value);
                    }
                }

                if (Marshal.IsComObject(pPropBag))
                {
                    Marshal.ReleaseComObject(pPropBag);
                }

                if (fClearDirty)
                {
                    _activeXState[s_isDirty] = false;
                }
            }

            /// <summary>
            ///  Fires the OnSave event to all of our IAdviseSink
            ///  listeners.  Used for ActiveXSourcing.
            /// </summary>
            private void SendOnSave()
            {
                int cnt = _adviseList.Count;
                for (int i = 0; i < cnt; i++)
                {
                    IAdviseSink s = _adviseList[i];
                    Debug.Assert(s is not null, "NULL in our advise list");
                    s.OnSave();
                }
            }

            /// <summary>
            ///  Implements IViewObject2::SetAdvise.
            /// </summary>
            internal HRESULT SetAdvise(Ole32.DVASPECT aspects, Ole32.ADVF advf, IAdviseSink pAdvSink)
            {
                // if it's not a content aspect, we don't support it.
                if ((aspects & Ole32.DVASPECT.CONTENT) == 0)
                {
                    return HRESULT.DV_E_DVASPECT;
                }

                // Set up some flags to return from GetAdvise.
                _activeXState[s_viewAdvisePrimeFirst] = (advf & Ole32.ADVF.PRIMEFIRST) != 0;
                _activeXState[s_viewAdviseOnlyOnce] = (advf & Ole32.ADVF.ONLYONCE) != 0;

                if (_viewAdviseSink is not null && Marshal.IsComObject(_viewAdviseSink))
                {
                    Marshal.ReleaseComObject(_viewAdviseSink);
                }

                _viewAdviseSink = pAdvSink;

                // prime them if they want it [we need to store this so they can get flags later]
                if (_activeXState[s_viewAdvisePrimeFirst])
                {
                    ViewChanged();
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Implements IOleObject::SetClientSite.
            /// </summary>
            internal void SetClientSite(Ole32.IOleClientSite? value)
            {
                if (_clientSite is not null)
                {
                    if (Marshal.IsComObject(_clientSite))
                    {
                        Marshal.FinalReleaseComObject(_clientSite);
                    }
                }

                _clientSite = value;

                if (_clientSite is not null)
                {
                    _control.Site = new AxSourcingSite(_control, _clientSite, "ControlAxSourcingSite");
                }
                else
                {
                    _control.Site = null;
                }

                // Get the ambient properties that effect us.
                if (GetAmbientProperty(Ole32.DispatchID.AMBIENT_UIDEAD, out object? obj))
                {
                    _activeXState[s_uiDead] = (bool)obj!;
                }

                if (_control is IButtonControl buttonControl && GetAmbientProperty(Ole32.DispatchID.AMBIENT_UIDEAD, out obj))
                {
                    buttonControl.NotifyDefault((bool)obj!);
                }

                if (_clientSite is null && _accelTable != IntPtr.Zero)
                {
                    PInvoke.DestroyAcceleratorTable(new HandleRef<HACCEL>(this, (HACCEL)_accelTable));
                    _accelTable = IntPtr.Zero;
                    _accelCount = -1;
                }

                _control.OnTopMostActiveXParentChanged(EventArgs.Empty);
            }

            /// <summary>
            ///  Implements IOleObject::SetExtent
            /// </summary>
            internal unsafe void SetExtent(Ole32.DVASPECT dwDrawAspect, Size* pSizel)
            {
                if ((dwDrawAspect & Ole32.DVASPECT.CONTENT) != 0)
                {
                    if (_activeXState[s_changingExtents])
                    {
                        return;
                    }

                    _activeXState[s_changingExtents] = true;

                    try
                    {
                        Size size = new Size(HiMetricToPixel(pSizel->Width, pSizel->Height));
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "SetExtent : new size:" + size.ToString());

                        // If we're in place active, let the in place site set our bounds.
                        // Otherwise, just set it on our control directly.
                        if (_activeXState[s_inPlaceActive])
                        {
                            if (_clientSite is Ole32.IOleInPlaceSite ioleClientSite)
                            {
                                Rectangle bounds = _control.Bounds;
                                bounds.Location = new Point(bounds.X, bounds.Y);
                                Size adjusted = new Size(size.Width, size.Height);
                                bounds.Width = adjusted.Width;
                                bounds.Height = adjusted.Height;
                                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "SetExtent : Announcing to in place site that our rect has changed.");
                                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "            Announcing rect = " + bounds);
                                Debug.Assert(_clientSite is not null, "How can we setextent before we are sited??");

                                RECT posRect = bounds;
                                ioleClientSite.OnPosRectChange(&posRect);
                            }
                        }

                        _control.Size = size;

                        // Check to see if the control overwrote our size with
                        // its own values.
                        if (!_control.Size.Equals(size))
                        {
                            Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "SetExtent : Control has changed size.  Setting dirty bit");
                            _activeXState[s_isDirty] = true;

                            // If we're not inplace active, then announce that the view changed.
                            if (!_activeXState[s_inPlaceActive])
                            {
                                ViewChanged();
                            }

                            // We need to call RequestNewObjectLayout
                            // here so we visually display our new extents.
                            if (!_activeXState[s_inPlaceActive] && _clientSite is not null)
                            {
                                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "SetExtent : Requesting new Object layout.");
                                _clientSite.RequestNewObjectLayout();
                            }
                        }
                    }
                    finally
                    {
                        _activeXState[s_changingExtents] = false;
                    }
                }
                else
                {
                    // We don't support any other aspects
                    ThrowHr(HRESULT.DV_E_DVASPECT);
                }
            }

            /// <summary>
            ///  Marks our state as in place visible.
            /// </summary>
            private void SetInPlaceVisible(bool visible)
            {
                _activeXState[s_inPlaceVisible] = visible;
                _control.Visible = visible;
            }

            /// <summary>
            ///  Implements IOleInPlaceObject::SetObjectRects.
            /// </summary>
            internal unsafe HRESULT SetObjectRects(RECT* lprcPosRect, RECT* lprcClipRect)
            {
                if (lprcPosRect is null || lprcClipRect is null)
                {
                    return HRESULT.E_INVALIDARG;
                }

#if DEBUG
                if (CompModSwitches.ActiveX.TraceInfo)
                {
                    Debug.WriteLine("SetObjectRects:");
                    Debug.Indent();

                    Debug.WriteLine("PosLeft:    " + lprcPosRect->left.ToString(CultureInfo.InvariantCulture));
                    Debug.WriteLine("PosTop:     " + lprcPosRect->top.ToString(CultureInfo.InvariantCulture));
                    Debug.WriteLine("PosRight:   " + lprcPosRect->right.ToString(CultureInfo.InvariantCulture));
                    Debug.WriteLine("PosBottom:  " + lprcPosRect->bottom.ToString(CultureInfo.InvariantCulture));

                    Debug.WriteLine("ClipLeft:   " + lprcClipRect->left.ToString(CultureInfo.InvariantCulture));
                    Debug.WriteLine("ClipTop:    " + lprcClipRect->top.ToString(CultureInfo.InvariantCulture));
                    Debug.WriteLine("ClipRight:  " + lprcClipRect->right.ToString(CultureInfo.InvariantCulture));
                    Debug.WriteLine("ClipBottom: " + lprcClipRect->bottom.ToString(CultureInfo.InvariantCulture));

                    Debug.Unindent();
                }
#endif

                Rectangle posRect = Rectangle.FromLTRB(lprcPosRect->left, lprcPosRect->top, lprcPosRect->right, lprcPosRect->bottom);

                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "Set control bounds: " + posRect.ToString());

                // ActiveX expects to be notified when a control's bounds change, and also
                // intends to notify us through SetObjectRects when we report that the
                // bounds are about to change.  We implement this all on a control's Bounds
                // property, which doesn't use this callback mechanism.  The adjustRect
                // member handles this. If it is non-null, then we are being called in
                // response to an OnPosRectChange call.  In this case we do not
                // set the control bounds but set the bounds on the adjustRect.  When
                // this returns from the container and comes back to our OnPosRectChange
                // implementation, these new bounds will be handed back to the control
                // for the actual window change.
                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "Old Control Bounds: " + _control.Bounds);
                if (_activeXState[s_adjustingRect])
                {
                    _adjustRect->left = posRect.X;
                    _adjustRect->top = posRect.Y;
                    _adjustRect->right = posRect.Right;
                    _adjustRect->bottom = posRect.Bottom;
                }
                else
                {
                    _activeXState[s_adjustingRect] = true;
                    try
                    {
                        _control.Bounds = posRect;
                    }
                    finally
                    {
                        _activeXState[s_adjustingRect] = false;
                    }
                }

                bool setRegion = false;

                if (_lastClipRect.HasValue)
                {
                    // We had a clipping rectangle, we need to set the Control's Region even if we don't have a new
                    // lprcClipRect to ensure it remove it in said case.
                    _lastClipRect = null;
                    setRegion = true;
                }

                if (lprcClipRect is not null)
                {
                    // The container wants us to clip, so figure out if we really need to.
                    Rectangle clipRect = *lprcClipRect;
                    Rectangle intersect;

                    // Trident always sends an empty ClipRect... and so, we check for that and not do an
                    // intersect in that case.
                    if (!clipRect.IsEmpty)
                    {
                        intersect = Rectangle.Intersect(posRect, clipRect);
                    }
                    else
                    {
                        intersect = posRect;
                    }

                    if (!intersect.Equals(posRect))
                    {
                        // Offset the rectangle back to client coordinates
                        RECT rcIntersect = intersect;
                        HWND hWndParent = PInvoke.GetParent(_control);

                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"Old Intersect: {rcIntersect}");
                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"New Control Bounds: {posRect}");

                        PInvoke.MapWindowPoints(hWndParent, _control, ref rcIntersect);

                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, $"New Intersect: {rcIntersect}");

                        _lastClipRect = rcIntersect;
                        setRegion = true;

                        Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "Created clipping region");
                    }
                }

                // If our region has changed, set the new value.  We only do this if
                // the handle has been created, since otherwise the control will
                // merge our region automatically.
                if (setRegion)
                {
                    _control.SetRegion(_control.Region);
                }

                // Yuck.  Forms^3 uses transparent overlay windows that appear to cause
                // painting artifacts.  Flicker like a banshee.
                _control.Invalidate();

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Throws the given hresult. This is used by ActiveX sourcing.
            /// </summary>
            internal static void ThrowHr(HRESULT hr)
            {
                throw new ExternalException(SR.ExternalException, (int)hr);
            }

            /// <summary>
            ///  Handles IOleControl::TranslateAccelerator
            /// </summary>
            internal unsafe HRESULT TranslateAccelerator(MSG* lpmsg)
            {
                if (lpmsg is null)
                {
                    return HRESULT.E_POINTER;
                }

#if DEBUG
                if (CompModSwitches.ActiveX.TraceInfo)
                {
                    if (!_control.IsHandleCreated)
                    {
                        Debug.WriteLine("AxSource: TranslateAccelerator before handle creation");
                    }
                    else
                    {
                        Message m = Message.Create(lpmsg->hwnd, lpmsg->message, lpmsg->wParam, lpmsg->lParam);
                        Debug.WriteLine($"AxSource: TranslateAccelerator : {m}");
                    }
                }
#endif // DEBUG

                bool needPreProcess = false;
                switch ((User32.WM)lpmsg->message)
                {
                    case User32.WM.KEYDOWN:
                    case User32.WM.SYSKEYDOWN:
                    case User32.WM.CHAR:
                    case User32.WM.SYSCHAR:
                        needPreProcess = true;
                        break;
                }

                Message msg = Message.Create(lpmsg->hwnd, lpmsg->message, lpmsg->wParam, lpmsg->lParam);
                if (needPreProcess)
                {
                    Control? target = FromChildHandle(lpmsg->hwnd);
                    if (_control == target || _control.Contains(target))
                    {
                        PreProcessControlState messageState = PreProcessControlMessageInternal(target, ref msg);
                        switch (messageState)
                        {
                            case PreProcessControlState.MessageProcessed:
                                // someone returned true from PreProcessMessage
                                // no need to dispatch the message, its already been coped with.
                                lpmsg->message = (uint)msg.MsgInternal;
                                lpmsg->wParam = msg.WParamInternal;
                                lpmsg->lParam = msg.LParamInternal;
                                return HRESULT.S_OK;
                            case PreProcessControlState.MessageNeeded:
                                // Here we need to dispatch the message ourselves
                                // otherwise the host may never send the key to our wndproc.

                                // Someone returned true from IsInputKey or IsInputChar
                                PInvoke.TranslateMessage(*lpmsg);
                                if (PInvoke.IsWindowUnicode(lpmsg->hwnd))
                                {
                                    User32.DispatchMessageW(ref *lpmsg);
                                }
                                else
                                {
                                    User32.DispatchMessageA(ref *lpmsg);
                                }

                                return HRESULT.S_OK;
                            case PreProcessControlState.MessageNotNeeded:
                                // in this case we'll check the site to see if it wants the message.
                                break;
                        }
                    }
                }

                // SITE processing.  We're not interested in the message, but the site may be.
                Debug.WriteLineIf(CompModSwitches.ActiveX.TraceInfo, "AxSource: Control did not process accelerator, handing to site");
                if (_clientSite is Ole32.IOleControlSite ioleClientSite)
                {
                    Ole32.KEYMODIFIERS keyState = 0;
                    if (PInvoke.GetKeyState(User32.VK.SHIFT) < 0)
                    {
                        keyState |= Ole32.KEYMODIFIERS.SHIFT;
                    }

                    if (PInvoke.GetKeyState(User32.VK.CONTROL) < 0)
                    {
                        keyState |= Ole32.KEYMODIFIERS.CONTROL;
                    }

                    if (PInvoke.GetKeyState(User32.VK.MENU) < 0)
                    {
                        keyState |= Ole32.KEYMODIFIERS.ALT;
                    }

                    return ioleClientSite.TranslateAccelerator(lpmsg, keyState);
                }

                return HRESULT.S_FALSE;
            }

            /// <summary>
            ///  Implements IOleInPlaceObject::UIDeactivate.
            /// </summary>
            internal HRESULT UIDeactivate()
            {
                // Only do this if we're UI active
                if (!_activeXState[s_uiActive])
                {
                    return HRESULT.S_OK;
                }

                _activeXState[s_uiActive] = false;

                // Notify frame windows, if appropriate, that we're no longer ui-active.
                _inPlaceUiWindow?.SetActiveObject(null, null);

                // May need this for SetActiveObject & OnUIDeactivate, so leave until function return
                Debug.Assert(_inPlaceFrame is not null, "No inplace frame -- how dod we go UI active?");
                _inPlaceFrame.SetActiveObject(null, null);

                if (_clientSite is Ole32.IOleInPlaceSite ioleClientSite)
                {
                    ioleClientSite.OnUIDeactivate(false);
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Implements IOleObject::Unadvise
            /// </summary>
            internal HRESULT Unadvise(uint dwConnection)
            {
                if (dwConnection > _adviseList.Count || _adviseList[(int)dwConnection - 1] is null)
                {
                    return HRESULT.OLE_E_NOCONNECTION;
                }

                IAdviseSink sink = _adviseList[(int)dwConnection - 1];
                _adviseList.RemoveAt((int)dwConnection - 1);
                if (Marshal.IsComObject(sink))
                {
                    Marshal.ReleaseComObject(sink);
                }

                return HRESULT.S_OK;
            }

            /// <summary>
            ///  Notifies our site that we have changed our size and location.
            /// </summary>
            internal unsafe void UpdateBounds(ref int x, ref int y, ref int width, ref int height, SET_WINDOW_POS_FLAGS flags)
            {
                if (!_activeXState[s_adjustingRect] && _activeXState[s_inPlaceVisible])
                {
                    if (_clientSite is Ole32.IOleInPlaceSite ioleClientSite)
                    {
                        var rc = new RECT();
                        if (flags.HasFlag(SET_WINDOW_POS_FLAGS.SWP_NOMOVE))
                        {
                            rc.left = _control.Left;
                            rc.top = _control.Top;
                        }
                        else
                        {
                            rc.left = x;
                            rc.top = y;
                        }

                        if (flags.HasFlag(SET_WINDOW_POS_FLAGS.SWP_NOSIZE))
                        {
                            rc.right = rc.left + _control.Width;
                            rc.bottom = rc.top + _control.Height;
                        }
                        else
                        {
                            rc.right = rc.left + width;
                            rc.bottom = rc.top + height;
                        }

                        // This member variable may be modified by SetObjectRects by the container.
                        _adjustRect = &rc;
                        _activeXState[s_adjustingRect] = true;

                        try
                        {
                            ioleClientSite.OnPosRectChange(&rc);
                        }
                        finally
                        {
                            _adjustRect = null;
                            _activeXState[s_adjustingRect] = false;
                        }

                        // On output, the new bounds will be reflected in  rc
                        if (!flags.HasFlag(SET_WINDOW_POS_FLAGS.SWP_NOMOVE))
                        {
                            x = rc.left;
                            y = rc.top;
                        }

                        if (!flags.HasFlag(SET_WINDOW_POS_FLAGS.SWP_NOSIZE))
                        {
                            width = rc.right - rc.left;
                            height = rc.bottom - rc.top;
                        }
                    }
                }
            }

            /// <summary>
            ///  Notifies that the accelerator table needs to be updated due to a change in a control mnemonic.
            /// </summary>
            internal void UpdateAccelTable()
            {
                // Setting the count to -1 will recreate the table on demand (when GetControlInfo is called).
                _accelCount = -1;

                if (_clientSite is Ole32.IOleControlSite ioleClientSite)
                {
                    ioleClientSite.OnControlInfoChanged();
                }
            }

            // Since this method is used by Reflection .. don't change the "signature"
            internal void ViewChangedInternal()
            {
                ViewChanged();
            }

            /// <summary>
            ///  Notifies our view advise sink (if it exists) that the view has
            ///  changed.
            /// </summary>
            private void ViewChanged()
            {
                // send the view change notification to anybody listening.
                //
                // Note: Word2000 won't resize components correctly if an OnViewChange notification
                //       is sent while the component is persisting it's state.  The !m_fSaving check
                //       is to make sure we don't call OnViewChange in this case.
                if (_viewAdviseSink is not null && !_activeXState[s_saving])
                {
                    _viewAdviseSink.OnViewChange((int)Ole32.DVASPECT.CONTENT, -1);

                    if (_activeXState[s_viewAdviseOnlyOnce])
                    {
                        if (Marshal.IsComObject(_viewAdviseSink))
                        {
                            Marshal.ReleaseComObject(_viewAdviseSink);
                        }

                        _viewAdviseSink = null;
                    }
                }
            }

            /// <summary>
            ///  Called when the window handle of the control has changed.
            /// </summary>
            void IWindowTarget.OnHandleChange(IntPtr newHandle)
            {
                _controlWindowTarget.OnHandleChange(newHandle);
            }

            /// <summary>
            ///  Called to do control-specific processing for this window.
            /// </summary>
            void IWindowTarget.OnMessage(ref Message m)
            {
                if (_activeXState[s_uiDead])
                {
                    if (m.IsMouseMessage())
                    {
                        return;
                    }

                    if (m.Msg >= (int)User32.WM.NCLBUTTONDOWN && m.Msg <= (int)User32.WM.NCMBUTTONDBLCLK)
                    {
                        return;
                    }

                    if (m.IsKeyMessage())
                    {
                        return;
                    }
                }

                _controlWindowTarget.OnMessage(ref m);
            }
        }
    }
}
