using Microsoft.AspNetCore.DataProtection;

namespace WorkLens.Services;

public sealed class AiSecretProtector(IDataProtectionProvider provider) : IAiSecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("WorkLens.AiProvider.ApiKey.v1");

    public string Protect(string value) => protector.Protect(value);

    public string Unprotect(string value) => protector.Unprotect(value);
}
