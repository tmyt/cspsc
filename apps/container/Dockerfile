FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN apt update && apt install -y gcc && apt clean
RUN dotnet publish -c Release -o /app/build

FROM alpine:latest AS runtime
RUN apk add --no-cache libc6-compat && ln -s /lib/libc.musl-x86_64.so.1 /lib/ld-linux-x86-64.so.2
WORKDIR /app
COPY --from=build /app/build/ .
EXPOSE 8081
ENTRYPOINT ["/app/cspsc"]
    