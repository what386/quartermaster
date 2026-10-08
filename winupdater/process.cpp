#include "updater.h"

#include <cerrno>
#include <limits>
#include <stdexcept>
#include <system_error>
#include <thread>

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace updater {
fs::path process_executable() {
  std::wstring buffer(32768, L'\0');
  const auto length = GetModuleFileNameW(nullptr, buffer.data(),
                                         static_cast<DWORD>(buffer.size()));
  if (length == 0 || length == buffer.size())
    throw std::runtime_error("Cannot locate the updater executable.");

  buffer.resize(length);
  return fs::canonical(buffer);
}

void wait_for_exit(std::uint32_t pid, std::chrono::milliseconds timeout) {
  if (pid == GetCurrentProcessId())
    throw std::runtime_error("Cannot wait for the updater itself.");

  const auto process = OpenProcess(SYNCHRONIZE, FALSE, pid);

  if (process == nullptr) {
    const auto error = GetLastError();
    if (error == ERROR_INVALID_PARAMETER)
      return; // The app has already exited.

    throw std::system_error(static_cast<int>(error), std::system_category(),
                            "Cannot wait for Quartermaster");
  }
  const auto result =
      WaitForSingleObject(process, static_cast<DWORD>(timeout.count()));
  const auto error = GetLastError();

  CloseHandle(process);

  if (result == WAIT_TIMEOUT)
    throw std::runtime_error(
        "Quartermaster did not exit before the update timeout.");

  if (result != WAIT_OBJECT_0)
    throw std::system_error(static_cast<int>(error), std::system_category(),
                            "Waiting for Quartermaster failed");
}

void restart(const fs::path &executable, const fs::path &working_directory) {
  std::wstring command = L"\"" + executable.native() + L"\"";
  STARTUPINFOW startup{};
  startup.cb = sizeof(startup);
  PROCESS_INFORMATION process{};
  if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr,
                      FALSE, 0, nullptr, working_directory.c_str(), &startup,
                      &process))
    throw std::system_error(static_cast<int>(GetLastError()),
                            std::system_category(),
                            "Cannot restart Quartermaster");

  CloseHandle(process.hThread);
  CloseHandle(process.hProcess);
}
} // namespace updater
