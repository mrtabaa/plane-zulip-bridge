FROM git.hallboard.ir/team/mirror-dotnet:10.0-sdk AS build

WORKDIR /src
COPY Plane.Zulip.Bridge.csproj .
RUN dotnet restore

COPY Program.cs ZulipMentions.cs PlaneCommentFormatter.cs PlaneMentionMapLoader.cs BridgeConfiguration.cs PmsPayload.cs IssueCacheStore.cs ./
COPY Properties ./Properties
RUN dotnet publish -c Release -o /app --no-restore


FROM git.hallboard.ir/team/mirror-dotnet-aspnet:10.0

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet","Plane.Zulip.Bridge.dll"]
