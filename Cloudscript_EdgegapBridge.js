var Edgegap_AppName = "APP_NAME";
var Edgegap_AppVersion = "APP_VERSION";
var Edgegap_ApiToken = "token XXXX"; //include the "token" keyword
var PlayFab_TitleSecret = "TITLE_SECRET";

handlers.StartEdgegapDeployment = function (args, context) {
    var QueueName = context.playStreamEvent.Payload.QueueName;
    var MatchId = context.playStreamEvent.Payload.MatchId;
    
    var GeoIpList = [];
    var PlayerIds = "";
    
    var MatchMember = multiplayer.GetMatch({
        QueueName: QueueName,
        MatchId: MatchId,
        EscapeObject: false,
        ReturnMemberAttributes: false
    }).Members;
    
    MatchMember.forEach(Member => {
        var Profile = entity.GetProfile({
            Entity: Member.Entity
        });
        
        var Location = server.GetPlayerProfile({
            PlayFabId: Profile.Profile.Lineage.MasterPlayerAccountId,
            ProfileConstraints: {
                ShowLocations:true
            }
        });
        
        GeoIpList.push({
            ip: "1.2.3.4",
            latitude: Location.PlayerProfile.Locations[0].Latitude,
            longitude: Location.PlayerProfile.Locations[0].Longitude
        });

        PlayerIds += Profile.Profile.Lineage.MasterPlayerAccountId + ",";
    });

    PlayerIds = PlayerIds.slice(0, -1);
    
    var response = JSON.parse(http.request(
        "https://api.edgegap.com/v1/deploy",
        "post",
        JSON.stringify({
            app_name: Edgegap_AppName, 
            version_name: Edgegap_AppVersion,
            geo_ip_list: [...GeoIpList],
            tags: [
                MatchId.slice(0, 20)
            ],
            env_vars: [
                {
                    key:"PLAYFAB_MATCH_ID",
                    value:MatchId
                },
                {
                    key:"PLAYFAB_TITLE_SECRET",
                    value:TitleSecret
                },
                {
                    key:"PLAYFAB_MATCH_PLAYERS_ID_LIST",
                    value:PlayerIds
                }
            ]
        }),
        "application/json",
        { "authorization": Edgegap_ApiToken }
    ));
    
    log.info("response", response); 
}