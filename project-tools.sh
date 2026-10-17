#!/bin/bash
set -euo pipefail

PROJECT_NAME="${PWD##*/}"
API_PROJECT="$PROJECT_NAME.Api"
INFRA_PROJECT="$PROJECT_NAME.Infrastructure"

case ${1:-} in
    run)
        echo -e "\033[0;36m→ Starting API… Scalar will be at /scalar\033[0m"
        (cd "$API_PROJECT" && dotnet run)
        ;;
    test)
        dotnet test
        ;;
    watch)
        echo -e "\033[0;36m→ Watching… Scalar at /scalar\033[0m"
        (cd "$API_PROJECT" && dotnet watch run)
        ;;
    migrate)
        # DbContext lives in Infrastructure; connection string/config lives in Api.
        # --project = where migrations are written, --startup-project = where it runs from.
        dotnet ef database update \
            --project "$INFRA_PROJECT" \
            --startup-project "$API_PROJECT"
        ;;
    migration)
        if [[ -z "${2:-}" ]]; then
            echo "Usage: ./project-tools.sh migration <MigrationName>"
            exit 1
        fi
        dotnet ef migrations add "$2" \
            --project "$INFRA_PROJECT" \
            --startup-project "$API_PROJECT"
        ;;
    build)
        dotnet build
        ;;
    clean)
        dotnet clean && rm -rf */bin */obj
        ;;
    *)
        echo "Usage: ./project-tools.sh {run|test|watch|migrate|migration <name>|build|clean}"
        ;;
esac
