@echo off
rem ClashUWP offline regression tests (no network, no deployment).
rem Runs the two source-linked harnesses: IpPacket checksum (G7) + DnsConfig (1.0.40).
setlocal
cd /d "%~dp0.."

echo === IpPacket checksum / multicast (G7) ===
dotnet run --project Tools\IpPacketChecksumTest
if errorlevel 1 goto :fail

echo.
echo === DnsConfig parse / validate / pool cascade (1.0.40) ===
dotnet run --project Tools\DnsConfigTest
if errorlevel 1 goto :fail

echo.
echo === Subscription name header parsing (1.0.72) ===
dotnet run --project Tools\SubscriptionNameTest
if errorlevel 1 goto :fail

echo.
echo ALL OFFLINE TESTS PASSED
exit /b 0

:fail
echo.
echo OFFLINE TESTS FAILED
exit /b 1
