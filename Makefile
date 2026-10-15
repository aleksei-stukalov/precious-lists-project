.PHONY: dev-api

dev-api:
	docker compose stop api
	docker compose up -d --wait db
	cd ./service && dotnet watch --project service.csproj run --launch-profile https

test-api:
	cd ./service && dotnet test tests/Service.Tests/Service.Tests.csproj