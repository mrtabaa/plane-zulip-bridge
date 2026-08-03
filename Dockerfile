FROM git.hallboard.ir/team/mirror-dotnet:10.0-sdk AS build

WORKDIR /src
COPY Plane.Zulip.Bridge.csproj .
RUN dotnet restore

COPY Program.cs ./
COPY Common ./Common
COPY Configuration ./Configuration
COPY Endpoints ./Endpoints
COPY Models ./Models
COPY Notifications ./Notifications
COPY Plane ./Plane
COPY Properties ./Properties
COPY Storage ./Storage
COPY Zulip ./Zulip
RUN dotnet publish -c Release -o /app --no-restore


FROM git.hallboard.ir/team/mirror-dotnet-aspnet:10.0

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet","Plane.Zulip.Bridge.dll"]
