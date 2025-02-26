﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Diagnostics;

namespace System.Windows.Forms
{
    // Miscellaneous utilities
    internal static class ImageListUtils
    {
        public static PropertyDescriptor? GetImageListProperty(PropertyDescriptor currentComponent, ref object instance)
        {
            if (instance is object[]) //multiple selection is not supported by this class
            {
                return null;
            }

            PropertyDescriptor? imageListProp = null;
            object? parentInstance = instance;

            if (currentComponent.TryGetAttribute(out RelatedImageListAttribute? relatedAttribute) && relatedAttribute.RelatedImageList is not null)
            {
                string[] pathInfo = relatedAttribute.RelatedImageList.Split('.');
                for (int i = 0; i < pathInfo.Length; i++)
                {
                    if (parentInstance is null)
                    {
                        Debug.Fail("A property specified in the path is null or not yet instantiated at this time");
                        break; // path is wrong
                    }

                    PropertyDescriptor? prop = TypeDescriptor.GetProperties(parentInstance)[pathInfo[i]];
                    if (prop is null)
                    {
                        Debug.Fail("The path specified to the property is wrong");
                        break; // path is wrong
                    }

                    if (i == pathInfo.Length - 1)
                    {
                        // we're on the last one, look if that's our guy
                        if (typeof(ImageList).IsAssignableFrom(prop.PropertyType))
                        {
                            instance = parentInstance;
                            imageListProp = prop;
                            break;
                        }
                    }
                    else
                    {
                        parentInstance = prop.GetValue(parentInstance);
                    }
                }
            }

            return imageListProp;
        }
    }
}
