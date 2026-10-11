-- Лаунчер «Valheim с модами»: проверяет доступ к микрофону и запускает игру с BepInEx.
-- macOS спрашивает доступ к микрофону от имени этого приложения, поэтому оно живёт, пока идёт игра.
-- __BRIDGE_DIR__ и __GAME_DIR__ подставляет build.sh.

property startedAt : missing value
property bridgeDir : "__BRIDGE_DIR__"
property gameDir : "__GAME_DIR__"
property checkOnlyFlag : "/tmp/vcomp_mic_check_only"

on run
	set startedAt to current date
	set micOk to true
	try
		do shell script "/usr/bin/arch -arm64 " & quoted form of (bridgeDir & "/.venv/bin/python") & " -I " & quoted form of (bridgeDir & "/mic_check.py") & " 30 > " & quoted form of (bridgeDir & "/data/mic_check.log") & " 2>&1"
	on error
		set micOk to false
	end try
	if not micOk then
		activate
		set answer to button returned of (display dialog "Нет доступа к микрофону — компаньон не услышит голос (текстовый чат работает). Разреши «Valheim с модами» в Настройках → Конфиденциальность и безопасность → Микрофон." buttons {"Открыть настройки", "Продолжить"} default button "Продолжить" with title "Valheim с модами")
		if answer is "Открыть настройки" then do shell script "open 'x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone'"
	end if
	-- touch /tmp/vcomp_mic_check_only — только проверить микрофон, игру не запускать
	set checkOnly to false
	try
		do shell script "test -f " & quoted form of checkOnlyFlag & " && rm -f " & quoted form of checkOnlyFlag
		set checkOnly to true
	end try
	if checkOnly then
		quit
		return
	end if
	do shell script "cd " & quoted form of gameDir & " && ./run_bepinex.sh > /dev/null 2>&1 &"
end run

on idle
	try
		do shell script "pgrep -x Valheim"
	on error
		if ((current date) - startedAt) > 90 then quit
	end try
	return 5
end idle
