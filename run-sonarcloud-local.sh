#!/bin/bash
# Local SonarCloud Analysis Script
# Run this to analyze code quality and coverage locally

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}=== CPMigrate SonarCloud Local Analysis ===${NC}"

# Check if token is set
if [ -z "$SONAR_TOKEN" ]; then
    echo -e "${RED}Error: SONAR_TOKEN environment variable not set${NC}"
    echo "Set it with: export SONAR_TOKEN=\"your_token_here\""
    echo "Get your token from: https://sonarcloud.io/account/security"
    exit 1
fi

# Check if scanner is installed
if ! command -v dotnet-sonarscanner &> /dev/null; then
    echo -e "${YELLOW}Installing dotnet-sonarscanner...${NC}"
    dotnet tool install --global dotnet-sonarscanner
fi

echo -e "${GREEN}1. Beginning SonarCloud analysis...${NC}"
dotnet sonarscanner begin \
  /k:"georgepwall1991_CPMigrate" \
  /o:"georgepwall1991" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.host.url="https://sonarcloud.io" \
  /d:sonar.sources="CPMigrate" \
  /d:sonar.tests="CPMigrate.Tests" \
  /d:sonar.exclusions="**/obj/**,**/bin/**,**/*.Designer.cs,**/nupkg/**" \
  /d:sonar.coverage.exclusions="**/*.Tests/**,**/Program.cs,**/TestDoubles/**" \
  /d:sonar.cs.opencover.reportsPaths="**/coverage.opencover.xml" \
  /d:sonar.cs.vstest.reportsPaths="**/*.trx"

echo -e "${GREEN}2. Building project...${NC}"
dotnet build --configuration Release

echo -e "${GREEN}3. Running tests with coverage...${NC}"
dotnet test \
  --configuration Release \
  --no-build \
  --logger "trx" \
  --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover

echo -e "${GREEN}4. Ending SonarCloud analysis (uploading results)...${NC}"
dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"

echo -e "${GREEN}=== Analysis Complete! ===${NC}"
echo -e "View results at: ${YELLOW}https://sonarcloud.io/project/overview?id=georgepwall1991_CPMigrate${NC}"
