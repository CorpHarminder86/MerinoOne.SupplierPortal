using System.Reflection;
using FluentValidation;
using MediatR;
using MerinoOne.SupplierPortal.Application.Common.Behaviours;
using Microsoft.Extensions.DependencyInjection;

namespace MerinoOne.SupplierPortal.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var asm = Assembly.GetExecutingAssembly();
        services.AddMediatR(c => c.RegisterServicesFromAssembly(asm));
        services.AddValidatorsFromAssembly(asm);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
        return services;
    }
}
