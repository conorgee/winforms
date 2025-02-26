﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Windows.Forms
{
    internal sealed partial class KeyboardToolTipStateMachine
    {
        internal enum SmState : byte
        {
            Hidden,
            ReadyForInitShow,
            Shown,
            ReadyForReshow,
            WaitForRefocus
        }
    }
}
