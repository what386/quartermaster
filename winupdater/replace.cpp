#include "updater.h"

#include <algorithm>
#include <cstdio>
#include <stdexcept>
#include <string>
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

namespace updater {
namespace {
bool within(const fs::path &child, const fs::path &parent) {
  auto child_part = child.begin();
  for (const auto &parent_part : parent) {
    if (child_part == child.end())
      return false;
    if (CompareStringOrdinal(child_part->c_str(), -1, parent_part.c_str(), -1,
                             TRUE) != CSTR_EQUAL)
      return false;
    ++child_part;
  }
  return true;
}

void check_target(const fs::path &target, const fs::path &relative) {
  auto current = target;
  for (const auto &part : relative) {
    current /= part;
    const auto status = fs::symlink_status(current);
    if (fs::is_symlink(status))
      throw std::runtime_error("An installation path is a symbolic link.");
    if (current != target / relative && fs::exists(status) &&
        !fs::is_directory(status))
      throw std::runtime_error(
          "An installation subdirectory is occupied by a file.");
  }
  const auto status = fs::symlink_status(target / relative);
  if (fs::exists(status) && !fs::is_regular_file(status))
    throw std::runtime_error(
        "An installation file is occupied by a directory or special file.");
}

struct Change {
  fs::path relative;
  bool backed_up = false;
  bool installed = false;
};

void create_parents(const fs::path &path, const fs::path &root,
                    std::vector<fs::path> &created) {
  auto current = root;
  for (const auto &part : path.lexically_relative(root)) {
    current /= part;
    if (fs::create_directory(current))
      created.push_back(current);
    if (!fs::is_directory(current) ||
        fs::is_symlink(fs::symlink_status(current)))
      throw std::runtime_error(
          "An installation directory changed during the update.");
  }
}
} // namespace

UpdatePlan prepare_update(const fs::path &source, const fs::path &target,
                          const fs::path &executable) {
  if (executable.empty() || executable != executable.filename() ||
      executable == "." || executable == "..")
    throw std::runtime_error("The application executable must be a filename.");
  UpdatePlan plan{fs::canonical(source), fs::canonical(target), executable, {}};
  if (!fs::is_directory(plan.source) || !fs::is_directory(plan.target))
    throw std::runtime_error(
        "Update and installation paths must be directories.");
  if (within(plan.source, plan.target) || within(plan.target, plan.source))
    throw std::runtime_error(
        "Update and installation directories must not overlap.");
  const auto self = process_executable();
  if (within(self, plan.source) || within(self, plan.target))
    throw std::runtime_error("Run a temporary copy of the updater outside the "
                             "update and installation directories.");
  for (const auto &entry : fs::recursive_directory_iterator(plan.source)) {
    const auto status = entry.symlink_status();
    if (fs::is_symlink(status) ||
        (!fs::is_directory(status) && !fs::is_regular_file(status)))
      throw std::runtime_error(
          "The staged update contains a symbolic link or special file.");
    if (!fs::is_regular_file(status))
      continue;
    const auto relative = entry.path().lexically_relative(plan.source);
    if (relative.begin()->string().rfind(".quartermaster-update-", 0) == 0)
      throw std::runtime_error(
          "The staged update contains a reserved backup path.");
    check_target(plan.target, relative);
    plan.files.push_back(relative);
  }
  if (std::find(plan.files.begin(), plan.files.end(), executable) ==
      plan.files.end())
    throw std::runtime_error(
        "The staged update is missing the application executable.");
  std::sort(plan.files.begin(), plan.files.end());
  return plan;
}

void apply_update(const UpdatePlan &plan) {
  fs::path backup;
  for (unsigned attempt = 0; attempt < 1000; ++attempt) {
    backup = plan.target / (".quartermaster-update-" + std::to_string(attempt));
    if (fs::create_directory(backup))
      break;
    if (attempt == 999)
      throw std::runtime_error(
          "Cannot create a fresh update backup directory.");
  }
  std::vector<Change> changes;
  std::vector<fs::path> directories;
  try {
    // Copy everything first; a staging/copy failure must not alter installed
    // files.
    for (const auto &relative : plan.files) {
      fs::create_directories((backup / "new" / relative).parent_path());
      fs::copy_file(plan.source / relative, backup / "new" / relative);
    }
    for (const auto &relative : plan.files) {
      check_target(plan.target, relative);
      create_parents((plan.target / relative).parent_path(), plan.target,
                     directories);
      changes.push_back({relative});
      auto &change = changes.back();
      if (fs::exists(plan.target / relative)) {
        fs::create_directories((backup / "old" / relative).parent_path());
        fs::rename(plan.target / relative, backup / "old" / relative);
        change.backed_up = true;
      }
      fs::rename(backup / "new" / relative, plan.target / relative);
      change.installed = true;
    }
    restart(plan.target / plan.executable, plan.target);
  } catch (...) {
    bool restored = true;
    for (auto change = changes.rbegin(); change != changes.rend(); ++change) {
      try {
        if (change->installed)
          fs::remove(plan.target / change->relative);
        if (change->backed_up)
          fs::rename(backup / "old" / change->relative,
                     plan.target / change->relative);
      } catch (const std::exception &error) {
        restored = false;
        std::fprintf(stderr, "Could not restore an original file: %s\n",
                     error.what());
      }
    }
    if (restored) {
      for (auto directory = directories.rbegin();
           directory != directories.rend(); ++directory) {
        std::error_code ignored;
        fs::remove(*directory, ignored);
      }
      std::error_code ignored;
      fs::remove_all(backup, ignored);
    } else {
      std::fprintf(stderr, "Update backup retained at: %s\n",
                   backup.u8string().c_str());
    }
    throw;
  }
  // Once the new app starts, cleanup failure must not trigger a rollback
  // underneath it.
  std::error_code cleanup_error;
  fs::remove_all(backup, cleanup_error);
  if (cleanup_error)
    std::fprintf(stderr, "Update installed; old backup cleanup failed: %s\n",
                 cleanup_error.message().c_str());
}
} // namespace updater
