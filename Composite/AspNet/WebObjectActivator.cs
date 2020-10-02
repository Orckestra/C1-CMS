using System;

using Microsoft.Extensions.DependencyInjection;

namespace Composite.AspNet
{
    internal class WebObjectActivator : IServiceProvider
    {
        private readonly IServiceProvider _inner;

        public WebObjectActivator(IServiceProvider inner)
        {
            _inner = inner;
        }

        public object GetService(Type serviceType)
        {
            var service = _inner.GetService(serviceType);
            if (service == null)
            {
                try
                {
                    service = ActivatorUtilities.CreateInstance(_inner, serviceType);
                }
                catch (InvalidOperationException)
                {
                    service = Activator.CreateInstance(serviceType, true);
                }
            }

            return service;
        }
    }
}
