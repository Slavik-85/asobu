# Wraps an already-staged tree (see build-packages.sh) into an rpm. Nothing is compiled here.
#
# AutoReqProv is off on purpose: left on, rpm reads .NET's native libraries and writes in
# requirements such as liblttng-ust.so.0, which no stock Fedora has, and the package refuses to
# install. Requirements are by soname because Fedora and openSUSE name the packages differently.

%global debug_package %{nil}
%global __os_install_post %{nil}
%define _build_id_links none
%define _binary_payload w6.xzdio

Name:           asobu
Version:        %{asobu_version}
Release:        1
Summary:        Minecraft: Java Edition launcher
License:        GPL-3.0-only
URL:            https://asobu.cc
BuildArch:      x86_64
AutoReqProv:    no
Requires:       libfontconfig.so.1()(64bit), libX11.so.6()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit), xdg-utils, ca-certificates
Recommends:     libicu, libssl.so.3()(64bit), xrandr, libsecret

%description
Isolated instances, every mod loader, mods from Modrinth and CurseForge, and crashes
explained in words.

%install
cp -a %{asobu_root}/. %{buildroot}/

%files
%defattr(-,root,root,-)
/opt/asobu
/usr/bin/asobu
/usr/share/applications/cc.asobu.Asobu.desktop
/usr/share/metainfo/cc.asobu.Asobu.metainfo.xml
/usr/share/icons/hicolor/*/apps/cc.asobu.Asobu.png
