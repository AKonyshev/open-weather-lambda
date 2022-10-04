## Prerequisites to install

- [NodeJS](https://nodejs.org/en/)
- [Serverless Framework CLI](https://serverless.com)
- [.NET Core 6](https://dotnet.microsoft.com/en-us/download/dotnet/6.0)

## Build Package

Mac OS or Linux

```bash
./build.sh
```

Windows

```bash
build.cmd
```

## Deploy via Serverless

```bash
serverless deploy
```

## Build Frontend
```bash
 docker-compose -f ./docker-compose.yml up --no-deps --build -d
 ```