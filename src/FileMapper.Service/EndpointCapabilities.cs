namespace FileMapper.Service;

/// <summary>Declares the operation's required role capability independently of its HTTP verb.</summary>
public sealed record EndpointCapability(string Name);

public static class EndpointCapabilities
{
    public static T RequireCapability<T>(this T endpoint, string capability) where T : IEndpointConventionBuilder
    {
        if (capability is not ("read" or "scan" or "plan" or "learn" or "execute" or "approve-destructive"))
            throw new ArgumentOutOfRangeException(nameof(capability), "Unknown FileMapper capability.");
        endpoint.Add(builder => builder.Metadata.Add(new EndpointCapability(capability)));
        return endpoint;
    }
}
