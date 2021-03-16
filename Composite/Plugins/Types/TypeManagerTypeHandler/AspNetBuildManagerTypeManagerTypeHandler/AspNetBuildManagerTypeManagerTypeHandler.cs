using System;
using System.Linq;
using System.Reflection;
using System.Web.Compilation;
using Composite.Core.Types.Plugins.TypeManagerTypeHandler;
using Microsoft.Practices.EnterpriseLibrary.Common.Configuration;
using Microsoft.Practices.EnterpriseLibrary.Common.Configuration.ObjectBuilder;
using System.Web.Hosting;


namespace Composite.Plugins.Types.TypeManagerTypeHandler.AspNetBuildManagerTypeManagerTypeHandler
{
    [ConfigurationElementType(typeof(AspNetBuildManagerTypeManagerTypeHandlerData))]
    internal sealed class AspNetBuildManagerTypeManagerTypeHandler : ITypeManagerTypeHandler
    {
        private static Assembly _globalResourcesAssembly;
        private static bool _assemblyFetchedOnce;
        private const string TYPE_NAME_PREFIX = "AspNetType:";

        public Type GetType(string fullName)
        {
            if (!HostingEnvironment.IsHosted)
                return null;

            var name = fullName;

            if (name.StartsWith(TYPE_NAME_PREFIX))
            {
                name = name.Remove(0, TYPE_NAME_PREFIX.Length);
            }

            return name.Contains(":") ? null : GetTypeFromName(name);
        }

        private static Type GetTypeFromName(string name)
        {
            var typeFromGlobalResources = GetGlobalResourcesAssembly()?.GetType(name, false, true);
            return typeFromGlobalResources != null ? typeFromGlobalResources : BuildManager.GetType(name, false, true);
        }

        private static Assembly GetGlobalResourcesAssembly()
        {
            if (_globalResourcesAssembly != null || _assemblyFetchedOnce)
                return _globalResourcesAssembly;

            _globalResourcesAssembly = BuildManager.GetReferencedAssemblies().OfType<Assembly>()
                .FirstOrDefault(assembly => assembly.FullName.StartsWith("App_GlobalResources"));
            _assemblyFetchedOnce = true;

            return _globalResourcesAssembly;
        }

        public string SerializeType(Type type)
        {
            if (!HostingEnvironment.IsHosted)
                return null;

            if (BuildManager.CodeAssemblies == null)
                return null;

            return BuildManager.CodeAssemblies.OfType<Assembly>().Any(assembly => assembly.GetTypes().Contains(type))
                ? $"{TYPE_NAME_PREFIX}{type.FullName}"
                : null;
        }

        public bool HasTypeWithName(string typeFullname)
        {
            if (!HostingEnvironment.IsHosted)
                return false;

            return GetTypeFromName(typeFullname) != null;
        }
    }

    [Assembler(typeof(NonConfigurableTypeManagerTypeHandlerAssembler))]
    internal sealed class AspNetBuildManagerTypeManagerTypeHandlerData : TypeManagerTypeHandlerData
    {
    }
}