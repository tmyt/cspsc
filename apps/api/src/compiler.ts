import { Container } from "@cloudflare/containers";

export class CompilerService extends Container {
    defaultPort = 8081;
    envVars = {
        "DOTNET_RUNNING_IN_CONTAINER": "true",
        "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT": "true"
    }
}
