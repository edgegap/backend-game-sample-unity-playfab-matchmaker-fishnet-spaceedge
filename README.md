# PlayFab Matchmaking bridge to Edgegap

This guide will show you how to set up a game with PlayFab so as to automatically deploy a server on Edgegap after players have been matched through the PlayFab matchmaker. The clients will access the information to connect to the server via the PlayFab Player Data, which will be updated by the server once it's ready.

This sample was tested using Unity v2021.3.10 and Fishnet v3.11.14 as its netcode.

## Tutorial

To make sure the project opens correctly in the Unity editor, clone the repo via the `git clone [URL]` command.

The Unity project is located in the `SpaceEdge_project` folder.

You can see the full documentation [here](https://docs.edgegap.com/docs/playfab-bridge).