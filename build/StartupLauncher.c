#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#define PATH_CAPACITY 32768

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR arguments, int show)
{
    (void)instance;
    (void)previous;
    (void)arguments;
    (void)show;

    static wchar_t directory[PATH_CAPACITY];
    static wchar_t application[PATH_CAPACITY];
    static wchar_t command[PATH_CAPACITY];
    static wchar_t windows[MAX_PATH];
    static wchar_t system[MAX_PATH];
    static wchar_t environment[4 * MAX_PATH + 96];
    DWORD length = GetModuleFileNameW(NULL, directory, ARRAYSIZE(directory));
    if (length == 0 || length >= ARRAYSIZE(directory))
        return ERROR_BAD_PATHNAME;
    wchar_t *separator = wcsrchr(directory, L'\\');
    if (separator == NULL)
        return ERROR_BAD_PATHNAME;
    *separator = L'\0';

    if (swprintf_s(application, ARRAYSIZE(application), L"%ls\\ZapretDesktop.exe", directory) < 0 ||
        swprintf_s(command, ARRAYSIZE(command), L"\"%ls\" --login", application) < 0)
        return ERROR_BAD_PATHNAME;

    UINT windowsLength = GetWindowsDirectoryW(windows, ARRAYSIZE(windows));
    UINT systemLength = GetSystemDirectoryW(system, ARRAYSIZE(system));
    if (windowsLength == 0 || windowsLength >= ARRAYSIZE(windows) ||
        systemLength == 0 || systemLength >= ARRAYSIZE(system))
        return ERROR_BAD_PATHNAME;

    // The scheduled task is elevated. Do not inherit per-user runtime or DLL search settings.
    int size = swprintf_s(environment, ARRAYSIZE(environment),
        L"PATH=%ls;%ls%lcSystemRoot=%ls%lcwindir=%ls%lc", system, windows, 0, windows, 0, windows, 0);
    if (size < 0)
        return ERROR_INSUFFICIENT_BUFFER;

    STARTUPINFOW startup = { 0 };
    PROCESS_INFORMATION process = { 0 };
    startup.cb = sizeof(startup);
    if (!CreateProcessW(application, command, NULL, NULL, FALSE, CREATE_UNICODE_ENVIRONMENT,
        environment, directory, &startup, &process))
        return (int)GetLastError();

    CloseHandle(process.hThread);
    DWORD result = ERROR_GEN_FAILURE;
    if (WaitForSingleObject(process.hProcess, INFINITE) == WAIT_OBJECT_0)
        GetExitCodeProcess(process.hProcess, &result);
    CloseHandle(process.hProcess);
    return (int)result;
}
