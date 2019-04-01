#include "APIHelper.h"

#include <stdlib.h>
#include <string.h>

namespace jni
{
	ListOfCleanables Class::g_AllClasses;

	Class::Class(const char* name, jclass clazz) : m_Class(clazz)
	{
		g_AllClasses.Add(this);
		m_ClassName = static_cast<char *>(malloc(strlen(name) + 1));
		strcpy(m_ClassName, name);
	}

	Class::~Class()
	{
		Cleanup();
	}

	void Class::Cleanup()
	{
		m_Class.Cleanup();
		if(m_ClassName != NULL)
		{
			free(m_ClassName);
			m_ClassName = NULL;
		}
		g_AllClasses.Remove(this);
	}

	void Class::CleanupAllClasses()
	{
		g_AllClasses.CleanupAll();
	}

}
