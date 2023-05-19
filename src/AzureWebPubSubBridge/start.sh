#!/bin/bash

set -x
# printenv | sort
FORWARDER=${FORWARDER:-"local"}
PROTOCOL=${PROTOCOL:-"direct"}
RELIABLE=${RELIABLE:-"false"}

if [[ $FORWARDER == "local" ]]; then
  dotnet AzureWebPubSubBridge.dll local --reliable="$RELIABLE" "$PROTOCOL"
else
  dotnet AzureWebPubSubBridge.dll remote --reliable="$RELIABLE"
fi
