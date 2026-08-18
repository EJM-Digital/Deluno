using Deluno.Intake.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Intake;

public static class IntakeServiceCollectionExtensions
{
    public static IServiceCollection AddDelunoIntakeModule(this IServiceCollection services)
    {
        services.AddSingleton<IIntakeRepository, SqliteIntakeRepository>();
        return services;
    }
}
