SHELL := /bin/bash
.DEFAULT_GOAL := help

# Prefer the user-local .NET 10 SDK installed without sudo. If it is absent the
# system dotnet is used; global.json will fail clearly when that SDK is too old.
DOTNET ?= $(HOME)/.dotnet/dotnet
ifeq ($(wildcard $(DOTNET)),)
DOTNET := dotnet
endif

# Homebrew's plain `ffmpeg` has no libass; `ffmpeg-full` is keg-only, so expose it here
# without editing the user's shell profile. Absent on other machines, it is a no-op.
ifneq (,$(wildcard /opt/homebrew/opt/ffmpeg-full/bin/ffmpeg))
export PATH := /opt/homebrew/opt/ffmpeg-full/bin:$(PATH)
endif

ifneq (,$(wildcard ./.env))
include .env
export
endif

# Local-only test connection (ignored by git). Included so `make test-integration`
# works without exporting a semicolon-containing value in the shell.
-include .env.test.local
export

.PHONY: help doctor build infra-up infra-down migrate owner-init test-unit test-integration test-media smoke-mock verify env-check clean

help:
	@echo "Content Factory — M0 commands"
	@echo "  make doctor            Check prerequisites (no install, no paid calls)"
	@echo "  make build             Build the solution"
	@echo "  make test-unit         Run unit tests (no DB, no network AI)"
	@echo "  make test-integration  Run PostgreSQL integration tests (test DB only)"
	@echo "  make test-media        Run native media tests (requires FFmpeg)"
	@echo "  make smoke-mock        Run the offline end-to-end pipeline 10 times (mock providers only)"
	@echo "  make verify            Build + unit tests + integration tests when configured"
	@echo "  make env-check         Verify local .env exists and is not tracked"

doctor:
	@PATH="$(HOME)/.dotnet:$$PATH" ./scripts/doctor.sh

infra-up: env-check
	docker compose -f infra/compose.yaml --env-file .env up -d --wait

infra-down:
	docker compose -f infra/compose.yaml --env-file .env down

migrate: env-check
	$(DOTNET) tool restore
	$(DOTNET) ef database update --project src/ContentFactory.Infrastructure --startup-project src/ContentFactory.Api

owner-init: env-check
	$(DOTNET) run --project src/ContentFactory.Cli -- init-owner

build:
	$(DOTNET) build ContentFactory.slnx --nologo

test-unit:
	$(DOTNET) test tests/ContentFactory.UnitTests --nologo

test-integration:
	@if [ -z "$$CF_TEST_POSTGRES" ]; then \
		echo "CF_TEST_POSTGRES is not set. Export a connection string to a PostgreSQL"; \
		echo "maintenance database (Database=postgres) whose name starts with cf_test_"; \
		echo "only for created test databases. See docs/START_HERE_DEEPSEEK.md."; \
		exit 2; \
	fi
	$(DOTNET) test tests/ContentFactory.IntegrationTests --nologo

test-media:
	@if ! command -v ffmpeg >/dev/null 2>&1 || ! command -v ffprobe >/dev/null 2>&1; then \
		echo "FFmpeg/FFprobe are not installed; media tests are blocked (not passed)."; \
		exit 2; \
	fi
	$(DOTNET) test tests/ContentFactory.MediaTests --nologo

smoke-mock:
	@./scripts/smoke-mock.sh $(ROUNDS)

verify: build test-unit

env-check:
	@test -f .env || (echo ".env is missing; copy .env.example and fill local values" && exit 2)
	@git check-ignore -q .env && echo ".env is ignored by git" || (echo ".env is NOT ignored by git" && exit 1)

clean:
	$(DOTNET) clean ContentFactory.slnx --nologo
