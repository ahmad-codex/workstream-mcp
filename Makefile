# Shortcuts for common tasks. Each target is a plain dotnet or docker command, so nothing
# here is required; see CONTRIBUTING.md.

SLN := workstream-mcp.sln
QUICKSTART := deploy/docker-compose.quickstart.yml

.PHONY: build test test-unit test-integration test-concurrency format format-check run up down

build:
	dotnet build $(SLN)

test-unit:
	dotnet test tests/Workstream.UnitTests

test-integration:
	dotnet test tests/Workstream.IntegrationTests

test-concurrency:
	dotnet test tests/Workstream.ConcurrencyTests

# Integration and concurrency tests need Docker.
test: test-unit test-integration test-concurrency

# Applies analyzer and code-style fixes from .editorconfig. Whitespace formatting is not
# applied because the codebase uses column alignment that dotnet format would undo.
format:
	dotnet format style $(SLN)
	dotnet format analyzers $(SLN)

format-check:
	dotnet format style $(SLN) --verify-no-changes
	dotnet format analyzers $(SLN) --verify-no-changes

# Runs the API against WORKSTREAM_DB_CONNECTION (set in the dev container).
run:
	dotnet run --project src/Workstream.Api

up:
	docker compose -f $(QUICKSTART) up -d --build

down:
	docker compose -f $(QUICKSTART) down
