import urllib.request, json, time
B="http://127.0.0.1:28999"
def g(p, t=1.0):
    try:
        return urllib.request.urlopen(B+p, timeout=t).read().decode("utf-8","replace")
    except Exception as e:
        return "ERR:"+str(e)[:50]
print("ping:", g("/ping"))
r = g("/get")
try:
    d=json.loads(r); print("get keys:", len(d), "InfiniteSun:", d.get("InfiniteSun"), "GameSpeed:", d.get("GameSpeed"))
except Exception as e: print("get parse:", e, "| head:", r[:80])
print("set InfiniteSun=1:", g("/set?name=InfiniteSun&val=1"))
time.sleep(0.3)
print("after set InfiniteSun:", json.loads(g("/get")).get("InfiniteSun"))
print("set GameSpeed=1.5:", g("/set?name=GameSpeed&val=1.5"))
time.sleep(0.3)
print("after set GameSpeed:", json.loads(g("/get")).get("GameSpeed"))
print("set NoCooldown=true:", g("/set?name=NoCooldown&val=true"))
time.sleep(0.3)
print("after set NoCooldown:", json.loads(g("/get")).get("NoCooldown"))
# 恢复
print("restore:", g("/set?name=InfiniteSun&val=0"), g("/set?name=GameSpeed&val=1.0"), g("/set?name=NoCooldown&val=false"))
