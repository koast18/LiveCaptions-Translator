Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoice('Microsoft Huihui Desktop')
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$out = 'tests/fixtures/iflytek-zh-cn-16k.wav'
$synth.SetOutputToWaveFile((Join-Path (Get-Location) $out), $fmt)
$synth.Speak('今天天气不错，我们一起去公园散步吧。')
$synth.SetOutputToNull()
$synth.Dispose()
Write-Output "wrote $out"
