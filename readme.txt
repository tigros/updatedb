Utility program to update a single folder or drive in a mysys database. Saves time if your system has millions of files.
On a newly created database, it will create 2 extra indexes which may take some time, but once they're created the next run(s) will be much faster.
This is for more advanced users with at least 5 million files, otherwise might as well just create a new database since it doesn't take very long.

The usage is simple: updatedb "X:\folder name" [-nr]
You can also use a dot to specify current folder, or a folder name relative to it.
Use quotes if the path contains spaces.
New in version 1.1, you can add -nr switch for non-recursive folder scan. It can be placed before or after the folder name.

Might be a good idea to use an elevated cmd prompt to include restricted files, if any.
Now in mysys, click Search to refresh affected tabs. If a drive was added or deleted, however, you'd need to open a new tab for it to be reflected in the Drives drop down list.

If you don't have mysys, give it a try, you won't regret it! You can read more about it and download from https://integritech.ca, it is free to try for a full year! Full featured! Not even a nag nor ads.

For convenience, signed copies of MysysSetup.zip and FoxitReader10.1.0.37527_Setup.zip are included in the Releases section.

Never have trouble finding (or playing, or viewing) your files again, system wide!

Rest assured there is no communication done by mysys to any server whatsoever, well apart from the built-in web browser which can, but it was more intended for viewing local files.

IntegriTech Inc.
https://integritech.ca