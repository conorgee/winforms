﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Collections;
using static Interop;

namespace System.Windows.Forms.ComponentModel.Com2Interop
{
    internal static partial class Com2TypeInfoProcessor
    {
        private class PropInfo
        {
            public const int ReadOnlyUnknown = 0;
            public const int ReadOnlyTrue = 1;
            public const int ReadOnlyFalse = 2;

            public string Name { get; set; }

            public Ole32.DispatchID DispId { get; set; } = Ole32.DispatchID.UNKNOWN;

            public Type ValueType { get; set; }

            public ArrayList Attributes { get; } = new ArrayList();

            public int ReadOnly { get; set; } = ReadOnlyUnknown;

            public bool IsDefault { get; set; }

            public object TypeData { get; set; }

            public bool NonBrowsable { get; set; }

            public int Index { get; set; }

            public override int GetHashCode() => Name?.GetHashCode() ?? base.GetHashCode();
        }
    }
}
