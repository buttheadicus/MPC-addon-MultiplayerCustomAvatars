using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace CustomAvatar.Utilities
{
    // 1.40.0 calls this FormatUsage. later 1.40 renamed it GraphicsFormatUsage. same values
    internal static class GraphicsFormatCompat
    {
        private static readonly object ReadPixelsValue;
        private static readonly object RenderValue;
        private static readonly MethodInfo IsSupportedMethod;
        private static readonly MethodInfo GetCompatibleMethod;

        static GraphicsFormatCompat()
        {
            var usageType = Type.GetType("UnityEngine.Experimental.Rendering.GraphicsFormatUsage, UnityEngine.CoreModule")
                            ?? Type.GetType("UnityEngine.Experimental.Rendering.FormatUsage, UnityEngine.CoreModule");
            if (usageType == null)
                return;

            ReadPixelsValue = Enum.Parse(usageType, "ReadPixels");
            RenderValue = Enum.Parse(usageType, "Render");
            foreach (var method in typeof(SystemInfo).GetMethods())
            {
                var parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[1].ParameterType != usageType)
                    continue;
                if (method.Name == "IsFormatSupported")
                    IsSupportedMethod = method;
                else if (method.Name == "GetCompatibleFormat")
                    GetCompatibleMethod = method;
            }
        }

        internal static bool SupportsReadPixels(GraphicsFormat format)
        {
            if (IsSupportedMethod == null || ReadPixelsValue == null)
                return false;
            return (bool)IsSupportedMethod.Invoke(null, new object[] { format, ReadPixelsValue });
        }

        internal static GraphicsFormat CompatibleRenderFormat(GraphicsFormat format)
        {
            if (GetCompatibleMethod == null || RenderValue == null)
                return format;
            return (GraphicsFormat)GetCompatibleMethod.Invoke(null, new object[] { format, RenderValue });
        }
    }
}
