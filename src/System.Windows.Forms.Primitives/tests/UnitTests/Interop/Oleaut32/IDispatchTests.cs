﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing;
using System.Windows.Forms.Primitives.Tests.Interop.Mocks;
using Windows.Win32.System.Com;
using Xunit;
using IDispatch = Interop.Oleaut32.IDispatch;
using IPictureDisp = Interop.Ole32.IPictureDisp;
using ITypeInfo = Interop.Oleaut32.ITypeInfo;
using DispatchID = Interop.Ole32.DispatchID;

namespace System.Windows.Forms.Primitives.Tests.Interop.Oleaut32
{
    [Collection("Sequential")]
    public partial class IDispatchTests
    {
        [StaFact]
        public unsafe void IDispatch_GetIDsOfNames_Invoke_Success()
        {
            using var image = new Bitmap(16, 32);
            IPictureDisp picture = MockAxHost.GetIPictureDispFromPicture(image);
            IDispatch dispatch = (IDispatch)picture;

            Guid riid = Guid.Empty;
            var rgszNames = new string[] { "Width", "Other" };
            var rgDispId = new DispatchID[rgszNames.Length];
            fixed (DispatchID* pRgDispId = rgDispId)
            {
                HRESULT hr = dispatch.GetIDsOfNames(&riid, rgszNames, (uint)rgszNames.Length, PInvoke.GetThreadLocale(), pRgDispId);
                Assert.Equal(HRESULT.S_OK, hr);
                Assert.Equal(new string[] { "Width", "Other" }, rgszNames);
                Assert.Equal(new DispatchID[] { (DispatchID)4, DispatchID.UNKNOWN }, rgDispId);
            }
        }

        [StaFact]
        public unsafe void IDispatch_GetTypeInfo_Invoke_Success()
        {
            using var image = new Bitmap(16, 16);
            IPictureDisp picture = MockAxHost.GetIPictureDispFromPicture(image);
            IDispatch dispatch = (IDispatch)picture;

            ITypeInfo typeInfo;
            HRESULT hr = dispatch.GetTypeInfo(0, PInvoke.GetThreadLocale(), out typeInfo);
            Assert.Equal(HRESULT.S_OK, hr);
            Assert.NotNull(typeInfo);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(typeInfo);
        }

        [StaFact]
        public unsafe void IDispatch_GetTypeInfoCount_Invoke_Success()
        {
            using var image = new Bitmap(16, 16);
            IPictureDisp picture = MockAxHost.GetIPictureDispFromPicture(image);
            IDispatch dispatch = (IDispatch)picture;

            uint ctInfo = uint.MaxValue;
            HRESULT hr = dispatch.GetTypeInfoCount(&ctInfo);
            Assert.Equal(HRESULT.S_OK, hr);
            Assert.Equal(1u, ctInfo);
        }

        [StaFact]
        public unsafe void IDispatch_Invoke_Invoke_Success()
        {
            using var image = new Bitmap(16, 32);
            IPictureDisp picture = MockAxHost.GetIPictureDispFromPicture(image);
            IDispatch dispatch = (IDispatch)picture;

            Guid riid = Guid.Empty;
            var dispParams = new global::Windows.Win32.System.Com.DISPPARAMS();
            var varResult = new object[1];
            var excepInfo = new EXCEPINFO();
            uint argErr = 0;
            HRESULT hr = dispatch.Invoke(
                (DispatchID)4,
                &riid,
                PInvoke.GetThreadLocale(),
                DISPATCH_FLAGS.DISPATCH_PROPERTYGET,
                &dispParams,
                varResult,
                &excepInfo,
                &argErr);
            Assert.Equal(HRESULT.S_OK, hr);
            Assert.Equal(16, GdiHelper.HimetricToPixelY((int)varResult[0]));
            Assert.Equal(0u, argErr);
        }
    }
}
