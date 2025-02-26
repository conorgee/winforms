﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Windows.Forms
{
    /// <summary>
    ///  Basic Properties for VScroll.
    /// </summary>
    public class VScrollProperties : ScrollProperties
    {
        public VScrollProperties(ScrollableControl? container) : base(container)
        {
        }

        private protected override int GetPageSize(ScrollableControl parent) => parent.ClientRectangle.Height;

        private protected override SCROLLBAR_CONSTANTS Orientation => SCROLLBAR_CONSTANTS.SB_VERT;

        private protected override int GetHorizontalDisplayPosition(ScrollableControl parent) => parent.DisplayRectangle.X;

        private protected override int GetVerticalDisplayPosition(ScrollableControl parent) => -_value;
    }
}
